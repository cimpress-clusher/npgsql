﻿#region License
// The PostgreSQL License
//
// Copyright (C) 2016 The Npgsql Development Team
//
// Permission to use, copy, modify, and distribute this software and its
// documentation for any purpose, without fee, and without a written
// agreement is hereby granted, provided that the above copyright notice
// and this paragraph and the following two paragraphs appear in all copies.
//
// IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY
// FOR DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES,
// INCLUDING LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS
// DOCUMENTATION, EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
// THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES,
// INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY
// AND FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
// ON AN "AS IS" BASIS, AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS
// TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using Npgsql.BackendMessages;
using Npgsql.PostgresTypes;
using NpgsqlTypes;

namespace Npgsql.TypeHandlers
{
    [TypeMapping("hstore", NpgsqlDbType.Hstore, new[] { typeof(Dictionary<string, string>), typeof(IDictionary<string, string>) })]
    class HstoreHandler : ChunkingTypeHandler<Dictionary<string, string>>,
        IChunkingTypeHandler<IDictionary<string, string>>, IChunkingTypeHandler<string>
    {
        ReadBuffer _readBuf;
        WriteBuffer _writeBuf;
        NpgsqlParameter _parameter;
        LengthCache _lengthCache;
        FieldDescription _fieldDescription;
        IDictionary<string, string> _value;
        IEnumerator<KeyValuePair<string, string>> _enumerator;
        string _key;
        int _numElements;
        State _state;

        /// <summary>
        /// The text handler to which we delegate encoding/decoding of the actual strings
        /// </summary>
        readonly TextHandler _textHandler;

        internal HstoreHandler(PostgresType postgresType, TypeHandlerRegistry registry) : base(postgresType)
        {
            _textHandler = new TextHandler(postgresType, registry);
        }

        #region Write

        public override int ValidateAndGetLength(object value, ref LengthCache lengthCache, NpgsqlParameter parameter = null)
        {
            if (lengthCache == null)
                lengthCache = new LengthCache(1);
            if (lengthCache.IsPopulated)
                return lengthCache.Get();

            var asDict = value as IDictionary<string, string>;
            if (asDict != null)
            {
                // Leave empty slot for the entire hstore length, and go ahead an populate the individual string slots
                var pos = lengthCache.Position;
                lengthCache.Set(0);

                var totalLen = 4;  // Number of key-value pairs
                foreach (var kv in asDict)
                {
                    totalLen += 8;   // Key length + value length
                    if (kv.Key == null)
                        throw new FormatException("HSTORE doesn't support null keys");
                    totalLen += lengthCache.Set(Encoding.UTF8.GetByteCount(kv.Key));
                    if (kv.Value != null)
                        totalLen += lengthCache.Set(Encoding.UTF8.GetByteCount(kv.Value));
                }

                return lengthCache.Lengths[pos] = totalLen;
            }

            throw CreateConversionException(value.GetType());
        }

        public override void PrepareWrite(object value, WriteBuffer buf, LengthCache lengthCache, NpgsqlParameter parameter = null)
        {
            _writeBuf = buf;
            _lengthCache = lengthCache;
            _parameter = parameter;
            _state = State.Count;

            var asDict = value as IDictionary<string, string>;
            if (asDict != null) {
                _value = asDict;
                return;
            }

            throw new InvalidOperationException("Internal Npgsql bug, please report.");
        }

        public override bool Write(ref DirectBuffer directBuf)
        {
            switch (_state)
            {
                case State.Count:
                    if (_writeBuf.WriteSpaceLeft < 4) { return false; }
                    _writeBuf.WriteInt32(_value.Count);
                    if (_value.Count == 0)
                    {
                        CleanupState();
                        return true;
                    }
                    _enumerator = _value.GetEnumerator();
                    _enumerator.MoveNext();
                    goto case State.KeyLen;

                case State.KeyLen:
                    _state = State.KeyLen;
                    if (_writeBuf.WriteSpaceLeft < 4) { return false; }
                    var keyLen = _lengthCache.Get();
                    _writeBuf.WriteInt32(keyLen);
                    _textHandler.PrepareWrite(_enumerator.Current.Key, _writeBuf, _lengthCache, _parameter);
                    goto case State.KeyData;

                case State.KeyData:
                    _state = State.KeyData;
                    if (!_textHandler.Write(ref directBuf)) { return false; }
                    goto case State.ValueLen;

                case State.ValueLen:
                    _state = State.ValueLen;
                    if (_writeBuf.WriteSpaceLeft < 4) { return false; }
                    if (_enumerator.Current.Value == null)
                    {
                        _writeBuf.WriteInt32(-1);
                        if (!_enumerator.MoveNext()) {
                            CleanupState();
                            return true;
                        }
                        goto case State.KeyLen;
                    }
                    var valueLen = _lengthCache.Get();
                    _writeBuf.WriteInt32(valueLen);
                    _textHandler.PrepareWrite(_enumerator.Current.Value, _writeBuf, _lengthCache, _parameter);
                    goto case State.ValueData;

                case State.ValueData:
                    _state = State.ValueData;
                    if (!_textHandler.Write(ref directBuf)) { return false; }
                    if (!_enumerator.MoveNext())
                    {
                        CleanupState();
                        return true;
                    }
                    goto case State.KeyLen;

                default:
                    throw new InvalidOperationException($"Internal Npgsql bug: unexpected value {_state} of enum {nameof(HstoreHandler)}.{nameof(State)}. Please file a bug.");
            }
        }

        #endregion

        #region Read

        public override void PrepareRead(ReadBuffer buf, int len, FieldDescription fieldDescription)
        {
            _readBuf = buf;
            _fieldDescription = fieldDescription;
            _state = State.Count;
        }

        public override bool Read([CanBeNull] out Dictionary<string, string> result)
        {
            result = null;
            switch (_state)
            {
            case State.Count:
                if (_readBuf.ReadBytesLeft < 4) { return false; }
                _numElements = _readBuf.ReadInt32();
                _value = new Dictionary<string, string>(_numElements);
                if (_numElements == 0)
                {
                    result = (Dictionary<string, string>)_value;
                    CleanupState();
                    return true;
                }
                goto case State.KeyLen;

            case State.KeyLen:
                _state = State.KeyLen;
                if (_readBuf.ReadBytesLeft < 4) { return false; }
                var keyLen = _readBuf.ReadInt32();
                Debug.Assert(keyLen != -1);
                _textHandler.PrepareRead(_readBuf, keyLen, _fieldDescription);
                goto case State.KeyData;

            case State.KeyData:
                _state = State.KeyData;
                if (!_textHandler.Read(out _key)) { return false; }
                goto case State.ValueLen;

            case State.ValueLen:
                _state = State.ValueLen;
                if (_readBuf.ReadBytesLeft < 4) { return false; }
                var valueLen = _readBuf.ReadInt32();
                if (valueLen == -1)
                {
                    _value[_key] = null;
                    if (--_numElements == 0)
                    {
                        result = (Dictionary<string, string>)_value;
                        CleanupState();
                        return true;
                    }
                    goto case State.KeyLen;
                }
                _textHandler.PrepareRead(_readBuf, valueLen, _fieldDescription);
                goto case State.ValueData;

            case State.ValueData:
                _state = State.ValueData;
                string value;
                if (!_textHandler.Read(out value)) { return false; }
                _value[_key] = value;
                if (--_numElements == 0)
                {
                    result = (Dictionary<string, string>)_value;
                    CleanupState();
                    return true;
                }
                goto case State.KeyLen;

            default:
                throw new InvalidOperationException($"Internal Npgsql bug: unexpected value {_state} of enum {nameof(HstoreHandler)}.{nameof(State)}. Please file a bug.");
            }
        }

        public bool Read([CanBeNull] out IDictionary<string, string> result)
        {
            Dictionary<string, string> result2;
            var completed = Read(out result2);
            result = result2;
            return completed;
        }

        public bool Read([CanBeNull] out string result)
        {
            IDictionary<string, string> dict;
            if (!((IChunkingTypeHandler<IDictionary<string, string>>) this).Read(out dict))
            {
                result = null;
                return false;
            }

            var sb = new StringBuilder();
            var i = dict.Count;
            foreach (var kv in dict)
            {
                sb.Append('"');
                sb.Append(kv.Key);
                sb.Append(@"""=>");
                if (kv.Value == null)
                    sb.Append("NULL");
                else
                {
                    sb.Append('"');
                    sb.Append(kv.Value);
                    sb.Append('"');
                }
                if (--i > 0)
                    sb.Append(',');
            }
            result = sb.ToString();
            return true;
        }

        #endregion

        void CleanupState()
        {
            _readBuf = null;
            _writeBuf = null;
            _value = null;
            _value = null;
            _parameter = null;
            _fieldDescription = null;
            _enumerator = null;
            _key = null;
        }

        enum State { Count, KeyLen, KeyData, ValueLen, ValueData }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Argus.Simulation.Core
{
    // Minimal RFC 8259 reader so the core can load data files without Unity or NuGet
    // dependencies. Produces Dictionary<string, object>, List<object>, double, string,
    // bool, or null.
    internal static class JsonReader
    {
        public static object Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            Parser parser = new Parser(text);
            object value = parser.ReadValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw parser.Error("unexpected trailing content");
            }

            return value;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _position;

            public Parser(string text)
            {
                _text = text;
            }

            public bool AtEnd => _position >= _text.Length;

            public object ReadValue()
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("unexpected end of input");
                }

                switch (_text[_position])
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': return ReadLiteral("true", true);
                    case 'f': return ReadLiteral("false", false);
                    case 'n': return ReadLiteral("null", null);
                    default: return ReadNumber();
                }
            }

            public void SkipWhitespace()
            {
                while (!AtEnd && (_text[_position] == ' ' || _text[_position] == '\t' ||
                                  _text[_position] == '\n' || _text[_position] == '\r'))
                {
                    _position++;
                }
            }

            public FormatException Error(string message) =>
                new FormatException($"Invalid JSON at offset {_position}: {message}.");

            private Dictionary<string, object> ReadObject()
            {
                Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    string key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    result[key] = ReadValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        return result;
                    }

                    Expect(',');
                }
            }

            private List<object> ReadArray()
            {
                List<object> result = new List<object>();
                Expect('[');
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    return result;
                }

                while (true)
                {
                    result.Add(ReadValue());
                    SkipWhitespace();
                    if (TryConsume(']'))
                    {
                        return result;
                    }

                    Expect(',');
                }
            }

            private string ReadString()
            {
                Expect('"');
                StringBuilder builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("unterminated string");
                    }

                    char current = _text[_position++];
                    if (current == '"')
                    {
                        return builder.ToString();
                    }

                    if (current != '\\')
                    {
                        builder.Append(current);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("unterminated escape");
                    }

                    char escape = _text[_position++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_position + 4 > _text.Length)
                            {
                                throw Error("truncated unicode escape");
                            }

                            builder.Append((char)int.Parse(
                                _text.Substring(_position, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            _position += 4;
                            break;
                        default:
                            throw Error($"invalid escape '\\{escape}'");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _position;
                while (!AtEnd && "+-0123456789.eE".IndexOf(_text[_position]) >= 0)
                {
                    _position++;
                }

                if (start == _position ||
                    !double.TryParse(
                        _text.Substring(start, _position - start),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double value))
                {
                    _position = start;
                    throw Error("expected a value");
                }

                return value;
            }

            private object ReadLiteral(string literal, object value)
            {
                if (string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
                {
                    throw Error($"expected '{literal}'");
                }

                _position += literal.Length;
                return value;
            }

            private void Expect(char expected)
            {
                if (AtEnd || _text[_position] != expected)
                {
                    throw Error($"expected '{expected}'");
                }

                _position++;
            }

            private bool TryConsume(char expected)
            {
                if (!AtEnd && _text[_position] == expected)
                {
                    _position++;
                    return true;
                }

                return false;
            }
        }
    }
}

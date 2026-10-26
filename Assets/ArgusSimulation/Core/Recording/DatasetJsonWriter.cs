using System;
using System.Globalization;
using System.Text;

namespace Argus.Simulation.Core
{
    // Compact JSON in the dataset v1 conventions (README.md): invariant round-trip numbers, null for
    // non-finite values, [x, y, z] vectors, [x, y, z, w] quaternions, nested row-major matrices and
    // UTC times with 7 fractional digits ending in Z. Core has no JSON library that also builds in
    // Unity, so this writes the text directly.
    internal sealed class DatasetJsonWriter
    {
        private const string UtcFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

        private readonly StringBuilder _text = new StringBuilder(512);
        private bool _needsComma;

        public DatasetJsonWriter BeginObject() => Open('{');
        public DatasetJsonWriter EndObject() => Close('}');
        public DatasetJsonWriter BeginArray() => Open('[');
        public DatasetJsonWriter EndArray() => Close(']');

        public DatasetJsonWriter Key(string name)
        {
            Separate();
            AppendString(name);
            _text.Append(':');
            _needsComma = false;
            return this;
        }

        public DatasetJsonWriter Null() => Raw("null");
        public DatasetJsonWriter Value(bool value) => Raw(value ? "true" : "false");
        public DatasetJsonWriter Value(long value) => Raw(value.ToString(CultureInfo.InvariantCulture));

        public DatasetJsonWriter Value(double value) =>
            double.IsNaN(value) || double.IsInfinity(value)
                ? Null()
                : Raw(RoundTrip(value));

        // "R" is the shortest round-trip form on .NET Core 3.0+, but older runtimes (Unity's Mono
        // among them) can return a string that parses back to a neighbouring double; "G17" always
        // round-trips.
        private static string RoundTrip(double value)
        {
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            return double.Parse(text, CultureInfo.InvariantCulture) == value
                ? text
                : value.ToString("G17", CultureInfo.InvariantCulture);
        }

        public DatasetJsonWriter Value(string value)
        {
            if (value == null)
            {
                return Null();
            }

            Separate();
            AppendString(value);
            _needsComma = true;
            return this;
        }

        public DatasetJsonWriter Value(Vector3d value) =>
            BeginArray().Value(value.X).Value(value.Y).Value(value.Z).EndArray();

        public DatasetJsonWriter Value(Quaterniond value) =>
            BeginArray().Value(value.X).Value(value.Y).Value(value.Z).Value(value.W).EndArray();

        public DatasetJsonWriter Value(Matrix3d value) =>
            BeginArray().Value(value.Row0).Value(value.Row1).Value(value.Row2).EndArray();

        public DatasetJsonWriter Utc(DateTimeOffset value) =>
            Value(value.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture));

        public override string ToString() => _text.ToString();

        private DatasetJsonWriter Open(char bracket)
        {
            Separate();
            _text.Append(bracket);
            _needsComma = false;
            return this;
        }

        private DatasetJsonWriter Close(char bracket)
        {
            _text.Append(bracket);
            _needsComma = true;
            return this;
        }

        private DatasetJsonWriter Raw(string token)
        {
            Separate();
            _text.Append(token);
            _needsComma = true;
            return this;
        }

        private void Separate()
        {
            if (_needsComma)
            {
                _text.Append(',');
            }
        }

        private void AppendString(string value)
        {
            _text.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"':
                        _text.Append("\\\"");
                        break;
                    case '\\':
                        _text.Append("\\\\");
                        break;
                    case '\n':
                        _text.Append("\\n");
                        break;
                    case '\r':
                        _text.Append("\\r");
                        break;
                    case '\t':
                        _text.Append("\\t");
                        break;
                    default:
                        if (character < ' ')
                        {
                            _text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _text.Append(character);
                        }

                        break;
                }
            }

            _text.Append('"');
        }
    }
}

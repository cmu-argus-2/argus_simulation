using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Argus.Simulation.Editor
{
    // A JSON tree for editing a run file in place (HostLauncherEditor). It keeps every field, in
    // order, including the ones the Inspector does not show, and keeps each untouched number's
    // text and whether each object and array was on one line, so saving an unedited run file
    // reproduces it byte for byte and an edit changes only its own values. Layout is the run
    // files': two-space indent, ", " between items, and "{ " and " }" around a one-line object.
    // The host's RunConfigurationFile still decides what is valid (--validate).
    internal abstract class RunFileJson
    {
        public static RunFileJson Parse(string text)
        {
            int position = 0;
            RunFileJson value = ParseValue(text, ref position);
            SkipWhitespace(text, ref position);
            if (position != text.Length)
            {
                throw Error(text, position, "unexpected text after the end of the JSON value");
            }

            return value;
        }

        public string Write()
        {
            StringBuilder output = new StringBuilder();
            Write(output, 0);
            return output.Append('\n').ToString();
        }

        internal abstract void Write(StringBuilder output, int indent);

        // A deep copy, layout included.
        public abstract RunFileJson Clone();

        public static Scalar Number(double value) => new Scalar(FormatNumber(value));

        // The run files' spelling: shortest round-trip text, with ".0" on a whole number.
        private static string FormatNumber(double value)
        {
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            return text.IndexOfAny(new[] { '.', 'E', 'e' }) < 0 ? text + ".0" : text;
        }

        public static Scalar String(string value) => new Scalar(Quote(value ?? ""));

        public static Scalar Flag(bool value) => new Scalar(value ? "true" : "false");

        private static RunFileJson ParseValue(string text, ref int position)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
            {
                throw Error(text, position, "the JSON ends early");
            }

            char c = text[position];
            if (c == '{')
            {
                return ParseObject(text, ref position);
            }

            if (c == '[')
            {
                return ParseArray(text, ref position);
            }

            int start = position;
            if (c == '"')
            {
                ReadString(text, ref position);
            }
            else
            {
                while (position < text.Length && "{}[],: \t\r\n".IndexOf(text[position]) < 0)
                {
                    position++;
                }

                if (position == start)
                {
                    throw Error(text, position, $"unexpected '{c}'");
                }
            }

            return new Scalar(text.Substring(start, position - start));
        }

        private static JsonObject ParseObject(string text, ref int position)
        {
            JsonObject result = new JsonObject();
            int open = position;
            position++;
            SkipWhitespace(text, ref position);
            if (position < text.Length && text[position] == '}')
            {
                position++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(text, ref position);
                if (position >= text.Length || text[position] != '"')
                {
                    throw Error(text, position, "expected a field name");
                }

                string name = ReadString(text, ref position);
                SkipWhitespace(text, ref position);
                Expect(text, ref position, ':');
                result.Entries.Add(new KeyValuePair<string, RunFileJson>(name, ParseValue(text, ref position)));
                SkipWhitespace(text, ref position);
                if (position < text.Length && text[position] == ',')
                {
                    position++;
                    continue;
                }

                Expect(text, ref position, '}');
                result.OneLine = IsOneLine(text, open, position);
                return result;
            }
        }

        private static JsonArray ParseArray(string text, ref int position)
        {
            JsonArray result = new JsonArray();
            int open = position;
            position++;
            SkipWhitespace(text, ref position);
            if (position < text.Length && text[position] == ']')
            {
                position++;
                return result;
            }

            while (true)
            {
                result.Items.Add(ParseValue(text, ref position));
                SkipWhitespace(text, ref position);
                if (position < text.Length && text[position] == ',')
                {
                    position++;
                    continue;
                }

                Expect(text, ref position, ']');
                result.OneLine = IsOneLine(text, open, position);
                return result;
            }
        }

        // The string's value, unescaped; position ends after the closing quote.
        private static string ReadString(string text, ref int position)
        {
            StringBuilder value = new StringBuilder();
            position++;
            while (position < text.Length)
            {
                char c = text[position++];
                if (c == '"')
                {
                    return value.ToString();
                }

                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                if (position >= text.Length)
                {
                    break;
                }

                char escaped = text[position++];
                switch (escaped)
                {
                    case 'n': value.Append('\n'); break;
                    case 't': value.Append('\t'); break;
                    case 'r': value.Append('\r'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'u':
                        if (position + 4 > text.Length)
                        {
                            throw Error(text, position, "a \\u escape needs four hex digits");
                        }

                        value.Append((char)int.Parse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        position += 4;
                        break;
                    default: value.Append(escaped); break;
                }
            }

            throw Error(text, position, "a string is not closed");
        }

        private static bool IsOneLine(string text, int open, int end) => text.IndexOf('\n', open, end - open) < 0;

        private static void Expect(string text, ref int position, char expected)
        {
            if (position >= text.Length || text[position] != expected)
            {
                throw Error(text, position, $"expected '{expected}'");
            }

            position++;
        }

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }

        private static FormatException Error(string text, int position, string message)
        {
            int line = 1;
            for (int i = 0; i < position && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return new FormatException($"line {line}: {message}");
        }

        private static string Quote(string value)
        {
            StringBuilder quoted = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': quoted.Append("\\\""); break;
                    case '\\': quoted.Append("\\\\"); break;
                    case '\n': quoted.Append("\\n"); break;
                    case '\r': quoted.Append("\\r"); break;
                    case '\t': quoted.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            quoted.Append(c);
                        }

                        break;
                }
            }

            return quoted.Append('"').ToString();
        }

        // A string, number, true, false or null, kept as its JSON text.
        internal sealed class Scalar : RunFileJson
        {
            public Scalar(string text)
            {
                Text = text;
            }

            public string Text { get; private set; }

            public bool IsString => Text.Length > 0 && Text[0] == '"';

            public string StringValue
            {
                get
                {
                    int position = 0;
                    return IsString ? ReadString(Text, ref position) : null;
                }
                set => Text = Quote(value ?? "");
            }

            // NaN when the text is not a number.
            public double NumberValue
            {
                get => double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
                set
                {
                    // Keep the text, and so the file, unchanged when the value is.
                    if (NumberValue.Equals(value))
                    {
                        return;
                    }

                    Text = FormatNumber(value);
                }
            }

            public bool IsFlag => Text == "true" || Text == "false";

            public bool FlagValue
            {
                get => Text == "true";
                set => Text = value ? "true" : "false";
            }

            public bool IsInteger => !IsString && long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            // 0 when the text is not a whole number; check IsInteger first.
            public long IntegerValue
            {
                get => long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
                set => Text = value.ToString(CultureInfo.InvariantCulture);
            }

            internal override void Write(StringBuilder output, int indent) => output.Append(Text);

            public override RunFileJson Clone() => new Scalar(Text);
        }

        internal sealed class JsonObject : RunFileJson
        {
            public List<KeyValuePair<string, RunFileJson>> Entries { get; } = new List<KeyValuePair<string, RunFileJson>>();

            public bool OneLine { get; set; }

            // Replaces the named field's value, or adds the field at the end.
            public void Set(string name, RunFileJson value)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (Entries[i].Key == name)
                    {
                        Entries[i] = new KeyValuePair<string, RunFileJson>(name, value);
                        return;
                    }
                }

                Entries.Add(new KeyValuePair<string, RunFileJson>(name, value));
            }

            public override RunFileJson Clone()
            {
                JsonObject copy = new JsonObject { OneLine = OneLine };
                foreach (KeyValuePair<string, RunFileJson> entry in Entries)
                {
                    copy.Entries.Add(new KeyValuePair<string, RunFileJson>(entry.Key, entry.Value.Clone()));
                }

                return copy;
            }

            public RunFileJson this[string name]
            {
                get
                {
                    foreach (KeyValuePair<string, RunFileJson> entry in Entries)
                    {
                        if (entry.Key == name)
                        {
                            return entry.Value;
                        }
                    }

                    return null;
                }
            }

            internal override void Write(StringBuilder output, int indent)
            {
                if (Entries.Count == 0)
                {
                    output.Append("{}");
                    return;
                }

                if (OneLine)
                {
                    output.Append("{ ");
                    for (int i = 0; i < Entries.Count; i++)
                    {
                        output.Append(i == 0 ? "" : ", ").Append(Quote(Entries[i].Key)).Append(": ");
                        Entries[i].Value.Write(output, indent);
                    }

                    output.Append(" }");
                    return;
                }

                output.Append("{\n");
                for (int i = 0; i < Entries.Count; i++)
                {
                    output.Append(' ', indent + 2).Append(Quote(Entries[i].Key)).Append(": ");
                    Entries[i].Value.Write(output, indent + 2);
                    output.Append(i + 1 < Entries.Count ? ",\n" : "\n");
                }

                output.Append(' ', indent).Append('}');
            }
        }

        internal sealed class JsonArray : RunFileJson
        {
            public List<RunFileJson> Items { get; } = new List<RunFileJson>();

            public bool OneLine { get; set; }

            public override RunFileJson Clone()
            {
                JsonArray copy = new JsonArray { OneLine = OneLine };
                foreach (RunFileJson item in Items)
                {
                    copy.Items.Add(item.Clone());
                }

                return copy;
            }

            internal override void Write(StringBuilder output, int indent)
            {
                if (OneLine || Items.Count == 0)
                {
                    output.Append('[');
                    for (int i = 0; i < Items.Count; i++)
                    {
                        output.Append(i == 0 ? "" : ", ");
                        Items[i].Write(output, indent);
                    }

                    output.Append(']');
                    return;
                }

                output.Append("[\n");
                for (int i = 0; i < Items.Count; i++)
                {
                    output.Append(' ', indent + 2);
                    Items[i].Write(output, indent + 2);
                    output.Append(i + 1 < Items.Count ? ",\n" : "\n");
                }

                output.Append(' ', indent).Append(']');
            }
        }
    }
}

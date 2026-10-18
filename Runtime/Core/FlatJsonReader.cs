using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XKDLabs.SimpleDataLogs.Core
{
    /// <summary>Reads a flat JSON object of strings, numbers and booleans; enough for the SDK's own state files.</summary>
    internal static class FlatJsonReader
    {
        /// <summary>Parses <c>{"key":value,...}</c> into raw values (string, double or bool); returns false on anything else.</summary>
        public static bool TryParse(string jsonText, out Dictionary<string, object> values)
        {
            values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (jsonText == null)
            {
                return false;
            }

            var cursor = new Cursor(jsonText);

            try
            {
                return TryParseObject(cursor, values);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Reads the whole object and checks nothing follows it.
        private static bool TryParseObject(Cursor cursor, Dictionary<string, object> values)
        {
            cursor.SkipWhitespace();
            cursor.Expect('{');
            cursor.SkipWhitespace();

            // An empty object is valid.
            if (cursor.Peek() == '}')
            {
                cursor.Advance();
                return cursor.IsAtEndAfterWhitespace();
            }

            while (true)
            {
                // Each member is a quoted key, a colon and a scalar value.
                cursor.SkipWhitespace();
                string memberKey = ReadString(cursor);
                cursor.SkipWhitespace();
                cursor.Expect(':');
                cursor.SkipWhitespace();
                values[memberKey] = ReadScalar(cursor);
                cursor.SkipWhitespace();

                // A comma means another member; a closing brace ends the object.
                char separator = cursor.Next();
                if (separator == '}')
                {
                    return cursor.IsAtEndAfterWhitespace();
                }

                if (separator != ',')
                {
                    return false;
                }
            }
        }

        // Reads a string, number or boolean.
        private static object ReadScalar(Cursor cursor)
        {
            char firstCharacter = cursor.Peek();

            if (firstCharacter == '"')
            {
                return ReadString(cursor);
            }

            if (cursor.TryConsumeWord("true"))
            {
                return true;
            }

            if (cursor.TryConsumeWord("false"))
            {
                return false;
            }

            return ReadNumber(cursor);
        }

        // Reads a JSON number as a double.
        private static double ReadNumber(Cursor cursor)
        {
            int numberStart = cursor.Position;
            while (!cursor.IsAtEnd && "+-0123456789.eE".IndexOf(cursor.Peek()) >= 0)
            {
                cursor.Advance();
            }

            string numberText = cursor.Text.Substring(numberStart, cursor.Position - numberStart);

            return double.Parse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // Reads a quoted string, decoding escapes.
        private static string ReadString(Cursor cursor)
        {
            cursor.Expect('"');
            var stringBuilder = new StringBuilder();

            while (true)
            {
                char character = cursor.Next();

                if (character == '"')
                {
                    return stringBuilder.ToString();
                }

                if (character != '\\')
                {
                    stringBuilder.Append(character);
                    continue;
                }

                stringBuilder.Append(ReadEscape(cursor));
            }
        }

        // Decodes the character after a backslash.
        private static char ReadEscape(Cursor cursor)
        {
            char escapeCode = cursor.Next();

            switch (escapeCode)
            {
                case '"':
                    return '"';
                case '\\':
                    return '\\';
                case '/':
                    return '/';
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'b':
                    return '\b';
                case 'f':
                    return '\f';
                case 'u':
                    string hexText = cursor.Text.Substring(cursor.Position, Math.Min(4, cursor.Text.Length - cursor.Position));
                    cursor.Advance(4);
                    return (char)int.Parse(hexText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                default:
                    throw new FormatException("Unknown escape");
            }
        }

        /// <summary>A read position in the text.</summary>
        private sealed class Cursor
        {
            /// <summary>Starts at the beginning of the text.</summary>
            public Cursor(string text)
            {
                Text = text;
            }

            /// <summary>The whole text.</summary>
            public string Text { get; }

            /// <summary>The index of the next character.</summary>
            public int Position { get; private set; }

            /// <summary>True when every character was read.</summary>
            public bool IsAtEnd => Position >= Text.Length;

            /// <summary>The next character, without reading it.</summary>
            public char Peek()
            {
                if (IsAtEnd)
                {
                    throw new FormatException("Unexpected end");
                }

                return Text[Position];
            }

            /// <summary>Reads the next character.</summary>
            public char Next()
            {
                char character = Peek();
                Position += 1;

                return character;
            }

            /// <summary>Skips characters.</summary>
            public void Advance(int characterCount = 1)
            {
                if (Position + characterCount > Text.Length)
                {
                    throw new FormatException("Unexpected end");
                }

                Position += characterCount;
            }

            /// <summary>Reads one expected character.</summary>
            public void Expect(char expectedCharacter)
            {
                if (Next() != expectedCharacter)
                {
                    throw new FormatException("Unexpected character");
                }
            }

            /// <summary>Reads a literal word when it is next.</summary>
            public bool TryConsumeWord(string word)
            {
                if (string.CompareOrdinal(Text, Position, word, 0, word.Length) != 0)
                {
                    return false;
                }

                Position += word.Length;
                return true;
            }

            /// <summary>Skips spaces, tabs and newlines.</summary>
            public void SkipWhitespace()
            {
                while (!IsAtEnd && char.IsWhiteSpace(Text[Position]))
                {
                    Position += 1;
                }
            }

            /// <summary>True when only whitespace remains.</summary>
            public bool IsAtEndAfterWhitespace()
            {
                SkipWhitespace();

                return IsAtEnd;
            }
        }
    }
}

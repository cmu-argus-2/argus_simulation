namespace Argus.Simulation.Core
{
    // Produces a single portable path component from user- or configuration-provided names.
    public static class OutputNameSanitizer
    {
        public static string Sanitize(string value, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value)
                ? fallback
                : value;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = "output";
            }

            char[] characters = candidate.ToCharArray();
            for (int index = 0; index < characters.Length; index++)
            {
                if (!char.IsLetterOrDigit(characters[index]) &&
                    characters[index] != '_' &&
                    characters[index] != '-')
                {
                    characters[index] = '_';
                }
            }

            return new string(characters);
        }
    }
}

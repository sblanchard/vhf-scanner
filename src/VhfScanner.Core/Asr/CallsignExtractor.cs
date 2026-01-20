using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace VhfScanner.Core.Asr;

/// <summary>
/// Extracts amateur radio callsigns from transcribed speech,
/// handling both direct callsigns and phonetic alphabet spelling
/// </summary>
public static partial class CallsignExtractor
{
    // ITU phonetic alphabet mapping
    private static readonly FrozenDictionary<string, char> PhoneticAlphabet = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
    {
        ["Alpha"] = 'A', ["Alfa"] = 'A',
        ["Bravo"] = 'B',
        ["Charlie"] = 'C',
        ["Delta"] = 'D',
        ["Echo"] = 'E',
        ["Foxtrot"] = 'F', ["Fox"] = 'F',
        ["Golf"] = 'G',
        ["Hotel"] = 'H',
        ["India"] = 'I',
        ["Juliet"] = 'J', ["Juliett"] = 'J',
        ["Kilo"] = 'K',
        ["Lima"] = 'L',
        ["Mike"] = 'M',
        ["November"] = 'N',
        ["Oscar"] = 'O',
        ["Papa"] = 'P',
        ["Quebec"] = 'Q',
        ["Romeo"] = 'R',
        ["Sierra"] = 'S',
        ["Tango"] = 'T',
        ["Uniform"] = 'U',
        ["Victor"] = 'V',
        ["Whiskey"] = 'W', ["Whisky"] = 'W',
        ["X-ray"] = 'X', ["Xray"] = 'X',
        ["Yankee"] = 'Y',
        ["Zulu"] = 'Z',
        // Numbers - various pronunciations
        ["Zero"] = '0', ["Oh"] = '0',
        ["One"] = '1', ["Wun"] = '1',
        ["Two"] = '2',
        ["Three"] = '3', ["Tree"] = '3',
        ["Four"] = '4', ["Fower"] = '4',
        ["Five"] = '5', ["Fife"] = '5',
        ["Six"] = '6',
        ["Seven"] = '7',
        ["Eight"] = '8', ["Ait"] = '8',
        ["Nine"] = '9', ["Niner"] = '9'
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Common callsign patterns by region
    // Format: 1-2 letter prefix + digit(s) + 1-4 letter suffix
    [GeneratedRegex(@"\b([A-Z]{1,2}\d{1,2}[A-Z]{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CallsignPattern();

    // French callsigns: F + digit + 1-4 letters (e.g., F4JZW)
    [GeneratedRegex(@"\b(F\d[A-Z]{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FrenchCallsignPattern();

    /// <summary>
    /// Extract callsign(s) from transcribed text
    /// </summary>
    public static IReadOnlyList<ExtractedCallsign> Extract(string transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            return [];

        var results = new List<ExtractedCallsign>();
        
        // First, try to find direct callsign matches
        var directMatches = CallsignPattern().Matches(transcription);
        foreach (Match match in directMatches)
        {
            var callsign = match.Value.ToUpperInvariant();
            if (IsValidCallsign(callsign))
            {
                results.Add(new ExtractedCallsign
                {
                    Callsign = callsign,
                    Confidence = 0.9,
                    ExtractionMethod = ExtractionMethod.Direct
                });
            }
        }

        // Then, try phonetic alphabet conversion
        var phoneticConverted = ConvertPhoneticToCallsign(transcription);
        if (!string.IsNullOrEmpty(phoneticConverted))
        {
            var phoneticMatches = CallsignPattern().Matches(phoneticConverted);
            foreach (Match match in phoneticMatches)
            {
                var callsign = match.Value.ToUpperInvariant();
                if (IsValidCallsign(callsign) && !results.Any(r => r.Callsign == callsign))
                {
                    results.Add(new ExtractedCallsign
                    {
                        Callsign = callsign,
                        Confidence = 0.7, // Lower confidence for phonetic extraction
                        ExtractionMethod = ExtractionMethod.Phonetic
                    });
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Convert phonetic alphabet words to letters/numbers
    /// </summary>
    private static string ConvertPhoneticToCallsign(string text)
    {
        var words = text.Split([' ', ',', '.', '-', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();
        var consecutivePhonetics = new StringBuilder();
        
        foreach (var word in words)
        {
            // Check if it's a phonetic word
            if (PhoneticAlphabet.TryGetValue(word.Trim(), out var letter))
            {
                consecutivePhonetics.Append(letter);
            }
            // Check if it's a single digit spoken as a word
            else if (int.TryParse(word, out var digit) && digit >= 0 && digit <= 9)
            {
                consecutivePhonetics.Append((char)('0' + digit));
            }
            // Check if it's already a single letter/digit
            else if (word.Length == 1 && char.IsLetterOrDigit(word[0]))
            {
                consecutivePhonetics.Append(char.ToUpperInvariant(word[0]));
            }
            else
            {
                // Not phonetic - if we have accumulated phonetics, add them
                if (consecutivePhonetics.Length > 0)
                {
                    result.Append(consecutivePhonetics);
                    result.Append(' ');
                    consecutivePhonetics.Clear();
                }
            }
        }

        // Don't forget trailing phonetics
        if (consecutivePhonetics.Length > 0)
        {
            result.Append(consecutivePhonetics);
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Validate if a string looks like a valid amateur radio callsign
    /// </summary>
    public static bool IsValidCallsign(string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign) || callsign.Length < 4 || callsign.Length > 7)
            return false;

        // Must contain at least one digit
        if (!callsign.Any(char.IsDigit))
            return false;

        // Must start with letter(s)
        if (!char.IsLetter(callsign[0]))
            return false;

        // Must end with letter(s)
        if (!char.IsLetter(callsign[^1]))
            return false;

        // Known invalid patterns (common misrecognitions)
        var invalidPatterns = new[] { "HELLO", "OVER", "ROGER", "COPY", "BREAK" };
        if (invalidPatterns.Any(p => callsign.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    /// <summary>
    /// Get the country/region prefix from a callsign
    /// </summary>
    public static string? GetPrefix(string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            return null;

        // Find where the digit is
        for (var i = 0; i < callsign.Length; i++)
        {
            if (char.IsDigit(callsign[i]))
            {
                return callsign[..i];
            }
        }

        return null;
    }
}

public sealed record ExtractedCallsign
{
    public required string Callsign { get; init; }
    public double Confidence { get; init; }
    public ExtractionMethod ExtractionMethod { get; init; }
}

public enum ExtractionMethod
{
    Direct,
    Phonetic
}

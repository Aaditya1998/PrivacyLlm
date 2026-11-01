namespace PrivacyLlm.Core.Classification;

/// <summary>Verhoeff checksum, used by UIDAI for the 12th digit of Aadhaar numbers.</summary>
public static class Verhoeff
{
    private static readonly int[,] D =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 },
        { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 }, { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 },
        { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 }, { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 },
        { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly int[,] P =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 },
        { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 }, { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 },
        { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    private static readonly int[] Inv = { 0, 4, 3, 2, 1, 5, 6, 7, 8, 9 };

    public static bool Validate(string digits)
    {
        if (digits.Length == 0 || !digits.All(char.IsDigit)) return false;
        var c = 0;
        for (var i = 0; i < digits.Length; i++)
            c = D[c, P[i % 8, digits[digits.Length - 1 - i] - '0']];
        return c == 0;
    }

    public static int ComputeCheckDigit(string digits)
    {
        var c = 0;
        for (var i = 0; i < digits.Length; i++)
            c = D[c, P[(i + 1) % 8, digits[digits.Length - 1 - i] - '0']];
        return Inv[c];
    }
}

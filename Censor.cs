using System.Text;
using System.Text.RegularExpressions;

namespace XlsxCensor;

/// <summary>
/// Sensor berbasis aturan (tanpa LLM) untuk pasangan kolom name/description.
/// </summary>
public static class Censor
{
    // Kata = rangkaian huruf/angka; tanda baca (spasi, titik, koma, /, -) jadi pemisah dan tidak disentuh.
    static readonly Regex TokenRx = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    // Minimal 8 digit berurutan, tidak menempel ke pemisah angka seperti / , . (tanggal, nominal, dll).
    static readonly Regex AccountRx = new(@"(?<![\d/,.])\d{8,}(?!\d|[/,.]\d)", RegexOptions.Compiled);

    // Kata umum: tidak cukup untuk dianggap match sendirian, tapi ikut disensor
    // kalau menempel dengan kata nama yang match (mis. "ABCDEFGH Company").
    static readonly HashSet<string> WeakWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "pt", "cv", "ud", "pd", "fa", "tbk", "persero", "koperasi", "yayasan",
        "co", "ltd", "inc", "llc", "corp", "corporation", "company", "group", "limited",
        "the", "and", "of", "dan", "di", "ke", "dari", "to", "from", "for", "untuk",
        "bank", "indonesia", "international", "internasional", "trf", "transfer",
    };

    // Kata sambung yang boleh dilewati saat mencocokkan singkatan (Bank of America = BA / BOA).
    static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "and", "dan", "the", "di",
    };

    const int MinPrefixLength = 4;   // "INDO" ~ "INDONESIA"
    const int MinAcronymLength = 3;  // "BCA" ~ "Bank Central Asia"
    const int MaxAcronymLength = 8;

    readonly record struct Token(int Start, int Length, string Text);

    /// <summary>
    /// Sensor 3-6 karakter di akhir kata, menyesuaikan panjang kata, usahakan masih ada huruf yang terlihat.
    /// "ABCDEFGH" -> "AB******", "Company" -> "C******", "ABCD" -> "A***", "ABC" -> "***".
    /// </summary>
    public static string MaskWord(string word)
    {
        int n = word.Length;
        if (n == 0) return word;
        int stars = n - 1 >= 3 ? Math.Min(6, n - 1) : Math.Min(3, n);
        return word[..(n - stars)] + new string('*', stars);
    }

    /// <summary>Sensor nomor rekening (>= 8 digit tanpa pemisah) di sebuah teks.</summary>
    public static string MaskAccounts(string text) =>
        AccountRx.Replace(text, m => MaskWord(m.Value));

    /// <summary>
    /// Sensor name dan description dari satu baris. Semua kata di name selalu disensor,
    /// kecuali name "Not Available".
    /// </summary>
    public static (string Name, string Description) CensorPair(string? name, string? description)
    {
        name ??= "";
        description ??= "";

        if (name.Trim().Equals("Not Available", StringComparison.OrdinalIgnoreCase))
            return (name, MaskAccounts(description));

        var nameTokens = Tokenize(name);
        var descTokens = Tokenize(description);
        var nameMask = new bool[nameTokens.Count];
        var descMask = new bool[descTokens.Count];

        MatchWords(nameTokens, descTokens, nameMask, descMask);
        // Singkatan di name, kepanjangan di description, dan sebaliknya.
        MatchAcronyms(nameTokens, descTokens, nameMask, descMask);
        MatchAcronyms(descTokens, nameTokens, descMask, nameMask);

        Array.Fill(nameMask, true);
        var newName = Apply(name, nameTokens, nameMask);
        var newDesc = MaskAccounts(Apply(description, descTokens, descMask));
        return (newName, newDesc);
    }

    static List<Token> Tokenize(string text) =>
        TokenRx.Matches(text).Select(m => new Token(m.Index, m.Length, m.Value)).ToList();

    static bool IsStrong(Token t) => t.Length >= 3 && !WeakWords.Contains(t.Text);

    static bool IsAlpha(string s) => s.All(char.IsLetter);

    static bool WordsMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        // Kata terpotong: salah satu adalah awalan dari yang lain.
        if (!IsAlpha(a) || !IsAlpha(b)) return false;
        var (shortW, longW) = a.Length <= b.Length ? (a, b) : (b, a);
        // Inisial satu huruf ("BUDI S" ~ "Budi Santoso"); dianggap lemah, jadi hanya ikut
        // tersensor kalau menempel dengan kata lain yang match.
        if (shortW.Length == 1) return char.ToUpperInvariant(longW[0]) == char.ToUpperInvariant(shortW[0]);
        return shortW.Length >= MinPrefixLength &&
               longW.StartsWith(shortW, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cocokkan kata per kata. Kata description yang match dikelompokkan jadi rangkaian berurutan;
    /// rangkaian diterima kalau ada minimal satu kata "kuat" (bukan kata umum seperti PT/Company).
    /// </summary>
    static void MatchWords(List<Token> nameTokens, List<Token> descTokens, bool[] nameMask, bool[] descMask)
    {
        var matchedNames = new List<int>[descTokens.Count];
        for (int i = 0; i < descTokens.Count; i++)
        {
            for (int j = 0; j < nameTokens.Count; j++)
            {
                if (WordsMatch(descTokens[i].Text, nameTokens[j].Text))
                    (matchedNames[i] ??= new List<int>()).Add(j);
            }
        }

        int k = 0;
        while (k < descTokens.Count)
        {
            if (matchedNames[k] is null) { k++; continue; }
            int start = k;
            while (k < descTokens.Count && matchedNames[k] is not null) k++;

            bool hasStrong = false;
            for (int i = start; i < k; i++)
                if (IsStrong(descTokens[i]) || matchedNames[i]!.Any(j => IsStrong(nameTokens[j])))
                    hasStrong = true;
            if (!hasStrong) continue;

            for (int i = start; i < k; i++)
            {
                descMask[i] = true;
                foreach (var j in matchedNames[i]!) nameMask[j] = true;
            }
        }
    }

    /// <summary>
    /// Token singkatan di <paramref name="acronyms"/> (mis. "BCA") dicocokkan dengan huruf depan
    /// kata-kata berurutan di <paramref name="expansions"/> (mis. "Bank Central Asia").
    /// </summary>
    static void MatchAcronyms(List<Token> acronyms, List<Token> expansions, bool[] acrMask, bool[] expMask)
    {
        for (int a = 0; a < acronyms.Count; a++)
        {
            var acr = acronyms[a].Text;
            if (acr.Length < MinAcronymLength || acr.Length > MaxAcronymLength) continue;
            if (!IsAlpha(acr) || WeakWords.Contains(acr)) continue;

            for (int start = 0; start < expansions.Count; start++)
            {
                int end = MatchInitials(acr, expansions, start);
                if (end < 0) continue;

                acrMask[a] = true;
                for (int i = start; i < end; i++) expMask[i] = true;
            }
        }
    }

    /// <summary>Kembalikan index akhir (eksklusif) kalau huruf depan cocok, -1 kalau tidak.</summary>
    static int MatchInitials(string acr, List<Token> words, int start)
    {
        int end = MatchInitialsFrom(acr, 0, words, start);
        // Singkatan yang kebetulan sama dengan satu kata utuh sudah ditangani MatchWords.
        return end - start >= 2 ? end : -1;
    }

    static int MatchInitialsFrom(string acr, int k, List<Token> words, int i)
    {
        if (k == acr.Length) return i;
        if (i >= words.Count) return -1;
        var w = words[i].Text;
        if (!char.IsLetter(w[0])) return -1;

        // Kata pendek yang sendirinya sudah singkatan menyumbang semua hurufnya ("MS First Capital" = "MSFC").
        if (w.Length is >= 2 and <= 3 && IsAlpha(w) && acr.Length - k >= w.Length &&
            string.Compare(acr, k, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0)
        {
            int end = MatchInitialsFrom(acr, k + w.Length, words, i + 1);
            if (end >= 0) return end;
        }
        if (char.ToUpperInvariant(w[0]) == char.ToUpperInvariant(acr[k]))
        {
            int end = MatchInitialsFrom(acr, k + 1, words, i + 1);
            if (end >= 0) return end;
        }
        if (k > 0 && Connectors.Contains(w))
            return MatchInitialsFrom(acr, k, words, i + 1);
        return -1;
    }

    static string Apply(string text, List<Token> tokens, bool[] mask)
    {
        if (!mask.Any(m => m)) return text;
        var sb = new StringBuilder(text);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!mask[i]) continue;
            var masked = MaskWord(tokens[i].Text);
            sb.Remove(tokens[i].Start, tokens[i].Length).Insert(tokens[i].Start, masked);
        }
        return sb.ToString();
    }
}

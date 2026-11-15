using System.Globalization;

namespace Freppuccino.UserFn;

internal enum Tk { Eof, Ident, Int, Float, Punct }

internal readonly record struct Token(Tk Kind, string Text, int Line, int Col, double Num = 0)
{
    public bool Is(string punctOrWord) => (Kind == Tk.Punct || Kind == Tk.Ident) && Text == punctOrWord;
    public override string ToString() => Kind == Tk.Eof ? "end of input" : $"'{Text}'";
}

internal static class Lexer
{
    static readonly string[] Puncts2 = ["++", "--", "+=", "-=", "*=", "/=", "%=", "==", "!=", "<=", ">=", "&&", "||", "^^", "<<", ">>"];
    const string Puncts1 = "+-*/%<>=!?:;,.(){}[]&|^~";

    public static List<Token> Lex(string src, List<UserFnDiagnostic> diags)
    {
        var toks = new List<Token>();
        int i = 0, line = 1, lineStart = 0;
        int Col(int at) => at - lineStart + 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { i++; line++; lineStart = i; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                int sl = line, sc = Col(i);
                i += 2;
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') { line++; lineStart = i + 1; }
                    i++;
                }
                if (i >= src.Length) { diags.Add(new(sl, sc, "unterminated comment")); break; }
                i += 2;
                continue;
            }
            int col = Col(i);
            if (c == '#') { diags.Add(new(line, col, "preprocessor directives are not supported")); while (i < src.Length && src[i] != '\n') i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                toks.Add(new(Tk.Ident, src[s..i], line, col));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int s = i;
                bool isFloat = false;
                while (i < src.Length && char.IsDigit(src[i])) i++;
                if (i < src.Length && src[i] == '.') { isFloat = true; i++; while (i < src.Length && char.IsDigit(src[i])) i++; }
                if (i < src.Length && (src[i] == 'e' || src[i] == 'E'))
                {
                    int save = i;
                    i++;
                    if (i < src.Length && (src[i] == '+' || src[i] == '-')) i++;
                    if (i < src.Length && char.IsDigit(src[i])) { isFloat = true; while (i < src.Length && char.IsDigit(src[i])) i++; }
                    else i = save;
                }
                string text = src[s..i];
                if (isFloat && i < src.Length && (src[i] == 'f' || src[i] == 'F')) i++;
                if (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_'))
                {
                    int e = i;
                    while (e < src.Length && (char.IsLetterOrDigit(src[e]) || src[e] == '_')) e++;
                    diags.Add(new(line, col, $"invalid number '{src[s..e]}'"));
                    i = e;
                    toks.Add(new(Tk.Float, text, line, col, 0));
                    continue;
                }
                if (isFloat)
                {
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
                    toks.Add(new(Tk.Float, text, line, col, d));
                }
                else
                {
                    if (text.Length > 1 && text[0] == '0') diags.Add(new(line, col, $"octal literal '{text}' is not supported"));
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) diags.Add(new(line, col, $"integer literal '{text}' is too large"));
                    toks.Add(new(Tk.Int, text, line, col, n));
                }
                continue;
            }
            if (i + 1 < src.Length && Array.IndexOf(Puncts2, src.Substring(i, 2)) >= 0)
            {
                toks.Add(new(Tk.Punct, src.Substring(i, 2), line, col));
                i += 2;
                continue;
            }
            if (Puncts1.IndexOf(c) >= 0) { toks.Add(new(Tk.Punct, c.ToString(), line, col)); i++; continue; }
            diags.Add(new(line, col, $"unexpected character '{c}'"));
            i++;
        }
        toks.Add(new(Tk.Eof, "", line, Col(i)));
        return toks;
    }
}

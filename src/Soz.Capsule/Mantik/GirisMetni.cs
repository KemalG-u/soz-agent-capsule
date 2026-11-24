using System.Text;

namespace Soz.Capsule;

/// <summary>Giriş satırının metin tamponu: ekleme, silme, ok tuşları, kelime silme. Pencere ve çizim yok (test edilebilir). Kısa, tek satırlık.</summary>
internal sealed class GirisMetni
{
    private readonly StringBuilder _sb = new();
    public int Imlec { get; private set; }
    public int EnFazla { get; set; } = 2000;
    public string Metin => _sb.ToString();
    public bool Bos => _sb.Length == 0;

    public void Ekle(string s)
    {
        foreach (var ch in s)
        {
            if (ch == '\r' || ch == '\n') continue; // tek satır
            if (char.IsControl(ch) && ch != '\t') continue;
            if (_sb.Length >= EnFazla) break;
            _sb.Insert(Imlec, ch == '\t' ? ' ' : ch); Imlec++;
        }
    }

    public bool GeriSil()
    {
        if (Imlec == 0) return false;
        var n = Imlec >= 2 && char.IsLowSurrogate(_sb[Imlec - 1]) && char.IsHighSurrogate(_sb[Imlec - 2]) ? 2 : 1;
        _sb.Remove(Imlec - n, n); Imlec -= n; return true;
    }

    public bool IleriSil()
    {
        if (Imlec >= _sb.Length) return false;
        var n = char.IsHighSurrogate(_sb[Imlec]) && Imlec + 1 < _sb.Length ? 2 : 1;
        _sb.Remove(Imlec, n); return true;
    }

    public void Sol(bool kelime = false) { Imlec = kelime ? KelimeBasi(Imlec) : Math.Max(0, Imlec - (Imlec >= 2 && char.IsLowSurrogate(_sb[Imlec - 1]) ? 2 : 1)); }
    public void Sag(bool kelime = false) { Imlec = kelime ? KelimeSonu(Imlec) : Math.Min(_sb.Length, Imlec + (Imlec < _sb.Length && char.IsHighSurrogate(_sb[Imlec]) ? 2 : 1)); }
    public void Basa() => Imlec = 0;
    public void Sona() => Imlec = _sb.Length;

    public void KelimeGeriSil()
    {
        var b = KelimeBasi(Imlec);
        if (b == Imlec) return;
        _sb.Remove(b, Imlec - b); Imlec = b;
    }

    public void Temizle() { _sb.Clear(); Imlec = 0; }

    private int KelimeBasi(int i)
    {
        while (i > 0 && char.IsWhiteSpace(_sb[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_sb[i - 1])) i--;
        return i;
    }

    private int KelimeSonu(int i)
    {
        while (i < _sb.Length && char.IsWhiteSpace(_sb[i])) i++;
        while (i < _sb.Length && !char.IsWhiteSpace(_sb[i])) i++;
        return i;
    }
}

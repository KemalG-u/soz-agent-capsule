using Soz.Core.Attention;

namespace Soz.Shell;

/// <summary>
/// Sunucudaki pencere uçlarını (<c>/api/live/focus</c>, <c>/api/sessions/{id}/focus</c>, <c>/api/attention/focus</c>) kabuğun pencere eşleştiricisine bağlar.
/// Sunucu kabukla aynı süreçte çalıştığından <see cref="SessionActions.Focuser"/> buraya yönlenir: proje klasörü ve oturum başlığı eşleştiriciye geçer,
/// dönüşte "doğru sekme seçildi mi" (<see cref="FocusResult.TabSelected"/>) ve belirsizlik ipucu sunucu yanıtına taşınır.
/// Eşleştirici ve ipucu gösterimi dışarıdan verilir (sahte eşleştiriciyle uçtan uca sınanır).
/// </summary>
internal static class OdakBaglayici
{
    /// <param name="eslestir">(pencere, süreç, proje klasörü, oturum başlığı) → sonuç. Gerçekte <see cref="PencereOnuneGetirici.Getir"/>.</param>
    /// <param name="klasorOku">Süreç numarasından çalışma klasörü (istekte proje klasörü yoksa). Boş verilebilir.</param>
    /// <param name="ipucuGoster">Başarılı ama belirsiz sonuçta kişiye söylenecek cümle (tepsi balonu). Boş verilebilir.</param>
    internal static Func<FocusRequest, FocusResult> Olustur(
        Func<long?, int?, string?, string?, PencereOnuneGetirici.Sonuc> eslestir,
        Func<int?, string?>? klasorOku = null,
        Action<string>? ipucuGoster = null)
        => istek =>
        {
            var klasor = istek.ProjectPath;
            if (string.IsNullOrWhiteSpace(klasor) && istek.Pid is > 0 && klasorOku != null)
            {
                try { klasor = klasorOku(istek.Pid); } catch (Exception) { klasor = null; }
            }
            var r = eslestir(istek.Hwnd, istek.Pid, klasor, istek.SessionTitle);
            Log.Write($"pencereyi aç: tür={r.Tur} başarılı={r.Basarili} pid={istek.Pid} hwnd={istek.Hwnd}");
            if (r.Basarili && !string.IsNullOrEmpty(r.Ipucu)) { try { ipucuGoster?.Invoke(r.Ipucu!); } catch (Exception) { } }
            return new FocusResult(r.Basarili, r.Pencere, r.Basarili && r.Tur == "sekme", r.Basarili ? r.Ipucu : null);
        };
}

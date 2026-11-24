using System.Runtime.CompilerServices;
using Soz.Core.Security;

namespace Soz.Core.Tests;

/// <summary>
/// Testler Claude Code içinden koşabilir; test sürecinin ata zincirinde claude bulunur ve çağıran "ajan" sayılırdı.
/// Bu yüzden ata zinciri denetimi yalnız testlerde kapalı başlar; ilgili testler açıkça yeniden açar (CallerIdentity.AncestorCheck).
/// Üretim kodunda bu bayrağı kapatan hiçbir yol yoktur.
/// </summary>
internal static class TestInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        CallerIdentity.AncestorCheck = false;
        Soz.Server.SozServer.AutoScanOnStartup = false;
    }
}

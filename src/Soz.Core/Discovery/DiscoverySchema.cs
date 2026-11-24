using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Discovery;

/// <summary>
/// Keşif tabloları. `SozDb.cs`'e dokunmamak için burada durur; sürüm göçüne bağlanması yönetici işidir ("şemaya bağlanacak").
/// Hepsi IF NOT EXISTS: tekrar çalıştırmak zararsızdır.
///
/// Yer değişmez ilkesinin (FABLE-VIZYON-5 ilke 1) veritabanındaki karşılığı: `discovery_slot` tablosuna yazma yalnız
/// <see cref="LayoutStore"/> içinden, kişinin açık eylemi sırasında açılan `discovery_guard` kapısıyla olur. Kapı kapalıyken
/// herhangi bir INSERT/UPDATE/DELETE "yuva yalnız kişi eyleminde değişir" diye reddedilir. Bu bir güvenlik duvarı değil özen katmanıdır
/// (aynı kullanıcı hesabı kapıyı elle açabilir); amaç başka koddaki hatanın yuvayı sessizce oynatmasını önlemektir.
/// </summary>
public static class DiscoverySchema
{
    public const string SlotTable = "discovery_slot";
    public const string GuardTable = "discovery_guard";

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS discovery_slot(
          kind_dir  INTEGER NOT NULL CHECK(kind_dir BETWEEN 0 AND 7),
          slot_dir  INTEGER NOT NULL CHECK(slot_dir BETWEEN 0 AND 7),
          object_uid TEXT NOT NULL CHECK(length(object_uid) BETWEEN 1 AND 200),
          source    TEXT NOT NULL CHECK(source IN ('fixed','user')),
          placed_at TEXT NOT NULL,
          PRIMARY KEY(kind_dir, slot_dir)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS discovery_guard(id INTEGER PRIMARY KEY CHECK(id=1), open INTEGER NOT NULL DEFAULT 0);
        INSERT OR IGNORE INTO discovery_guard(id, open) VALUES(1, 0);
        CREATE TRIGGER IF NOT EXISTS trg_discovery_slot_ins BEFORE INSERT ON discovery_slot
          WHEN (SELECT open FROM discovery_guard WHERE id=1) <> 1
          BEGIN SELECT RAISE(ABORT, 'yuva yalniz kisi eyleminde degisir'); END;
        CREATE TRIGGER IF NOT EXISTS trg_discovery_slot_upd BEFORE UPDATE ON discovery_slot
          WHEN (SELECT open FROM discovery_guard WHERE id=1) <> 1
          BEGIN SELECT RAISE(ABORT, 'yuva yalniz kisi eyleminde degisir'); END;
        CREATE TRIGGER IF NOT EXISTS trg_discovery_slot_del BEFORE DELETE ON discovery_slot
          WHEN (SELECT open FROM discovery_guard WHERE id=1) <> 1
          BEGIN SELECT RAISE(ABORT, 'yuva yalniz kisi eyleminde degisir'); END;

        CREATE TABLE IF NOT EXISTS discovery_active_day(day TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS discovery_usage(
          object_uid TEXT NOT NULL,
          scope      TEXT NOT NULL CHECK(scope IN ('global','project','time')),
          scope_key  TEXT NOT NULL DEFAULT '',
          f          REAL NOT NULL,
          t_last     REAL NOT NULL,
          n          INTEGER NOT NULL,
          PRIMARY KEY(object_uid, scope, scope_key)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_discovery_usage_scope ON discovery_usage(scope, scope_key);

        CREATE TABLE IF NOT EXISTS discovery_dismiss(
          object_uid  TEXT NOT NULL,
          project_uid TEXT NOT NULL DEFAULT '',
          mode        TEXT NOT NULL CHECK(mode IN ('penalty','block')),
          c           REAL NOT NULL DEFAULT 0,
          t_last      REAL NOT NULL DEFAULT 0,
          created_at  TEXT NOT NULL,
          PRIMARY KEY(object_uid, project_uid, mode)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS discovery_batch(
          batch_uid   TEXT PRIMARY KEY,
          ts          TEXT NOT NULL,
          project_uid TEXT NOT NULL DEFAULT '',
          shown       INTEGER NOT NULL CHECK(shown IN (0,1)),
          chosen_uid  TEXT NULL,
          chosen_ts   TEXT NULL
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_discovery_batch_open ON discovery_batch(project_uid, ts) WHERE chosen_uid IS NULL;
        CREATE TABLE IF NOT EXISTS discovery_impression(
          batch_uid  TEXT NOT NULL,
          rank       INTEGER NOT NULL,
          object_uid TEXT NOT NULL,
          score      REAL NOT NULL,
          PRIMARY KEY(batch_uid, rank)
        ) WITHOUT ROWID;
        """;

    /// <summary>Olay türleri (event_kind tablosu varsa oraya da kaydedilir).</summary>
    public static readonly (string Kind, string Description)[] EventKinds =
    {
        ("discovery.slot.placed", "Kişi bir yuvaya nesne yerleştirdi (kind_dir, slot_dir, object_uid, replaced)."),
        ("discovery.slot.cleared", "Kişi bir yuvayı boşalttı (kind_dir, slot_dir, object_uid)."),
        ("discovery.used", "Bir nesne kişinin eliyle kullanıldı (object_uid, source). Öneri sayaçlarını besler."),
        ("discovery.suggestion.dismissed", "Kişi bir öneriyi susturdu (object_uid, mode, project)."),
        ("discovery.suggestion.restored", "Kişi susturulmuş bir öneriyi geri aldı (object_uid, mode, project)."),
    };

    /// <summary>Tabloları kurar. Çağıranın işleminde (tx) çalışır; yönetici bunu SozDb göçüne tek satırla bağlar.</summary>
    public static void Apply(SqliteConnection c, SqliteTransaction tx)
    {
        c.Exec(Ddl, tx);
        if (c.Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name='event_kind'", tx) is not null)
            foreach (var (kind, desc) in EventKinds)
                c.Exec("INSERT OR IGNORE INTO event_kind(kind, owner, schema_v, since, description) VALUES(@k,'soz.discovery',1,@t,@d)", tx, ("@k", kind), ("@t", Time.NowIso()), ("@d", desc));
    }

    /// <summary>Keşif kurulu mu? Uç noktalar tablo bağlanmadan çökmesin diye buna bakar.</summary>
    public static bool IsInstalled(SqliteConnection c)
        => c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('discovery_slot','discovery_guard','discovery_usage','discovery_active_day','discovery_dismiss','discovery_batch','discovery_impression')") == 7;
}

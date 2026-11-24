// Test çıktısından tek cümlelik özet: kaç test geçti, kaç tane kaldı, kaç tane atlandı.
// Tanınan biçimler: jest/vitest, node:test (TAP), pytest, dotnet test (Türkçe ve İngilizce), cargo test, go test, mocha.
// Tanınmazsa null döner; kart yalnız "ayrıntı" bağlantısını gösterir.
export interface TestCounts {
  passed: number
  failed: number
  skipped: number
}

const num = (s: string | undefined) => (s === undefined ? 0 : Number(s))

export function parseCounts(out: string | null | undefined): TestCounts | null {
  if (!out) return null
  let m: RegExpMatchArray | null

  // dotnet: "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5" ya da Türkçe "Başarılı! - Başarısız: 0, Başarılı: 5, Atlandı: 0"
  m = out.match(/(?:Failed|Başarısız):\s*(\d+),\s*(?:Passed|Başarılı|Geçti):\s*(\d+),\s*(?:Skipped|Atlandı):\s*(\d+)/i)
  if (m) return { failed: num(m[1]), passed: num(m[2]), skipped: num(m[3]) }

  // node:test: "# pass 3" "# fail 0" "# skipped 1"
  const np = out.match(/^#\s*pass\s+(\d+)/im)
  if (np) return { passed: num(np[1]), failed: num(out.match(/^#\s*fail\s+(\d+)/im)?.[1]), skipped: num(out.match(/^#\s*skipped\s+(\d+)/im)?.[1]) }

  // jest / vitest: "Tests:  1 failed, 2 skipped, 11 passed, 14 total"
  m = out.match(/^\s*Tests:\s*(.+)$/im)
  if (m) {
    const line = m[1]
    return { passed: num(line.match(/(\d+)\s+passed/)?.[1]), failed: num(line.match(/(\d+)\s+failed/)?.[1]), skipped: num(line.match(/(\d+)\s+(?:skipped|todo)/)?.[1]) }
  }

  // cargo: "test result: ok. 5 passed; 0 failed; 1 ignored"
  m = out.match(/test result:\s*\w+\.\s*(\d+)\s+passed;\s*(\d+)\s+failed;\s*(\d+)\s+ignored/i)
  if (m) return { passed: num(m[1]), failed: num(m[2]), skipped: num(m[3]) }

  // pytest: "2 failed, 14 passed, 1 skipped in 3.1s"
  m = out.match(/(?:^|\n)=*\s*((?:\d+\s+(?:failed|passed|skipped|error|errors|xfailed|xpassed|deselected)(?:,\s*)?)+)\s+in\s+[\d.]+s/i)
  if (m) {
    const l = m[1]
    return { passed: num(l.match(/(\d+)\s+passed/)?.[1]), failed: num(l.match(/(\d+)\s+(?:failed|error)/)?.[1]), skipped: num(l.match(/(\d+)\s+skipped/)?.[1]) }
  }

  // mocha: "3 passing", "1 failing", "2 pending"
  const mp = out.match(/(\d+)\s+passing/)
  if (mp) return { passed: num(mp[1]), failed: num(out.match(/(\d+)\s+failing/)?.[1]), skipped: num(out.match(/(\d+)\s+pending/)?.[1]) }

  // go: "ok  pkg  0.2s" / "FAIL pkg"
  if (/^(ok|FAIL)\s+\S+\s+[\d.]+s/m.test(out)) {
    const ok = (out.match(/^ok\s+\S+/gm) ?? []).length
    const bad = (out.match(/^FAIL\s+\S+/gm) ?? []).length
    return { passed: ok, failed: bad, skipped: 0 }
  }
  return null
}

/** Kalan ilk testin satırı: kısa, tek satır. */
export function firstFailure(out: string | null | undefined): string | null {
  if (!out) return null
  const lines = out.split(/\r?\n/)
  for (let i = 0; i < lines.length; i++) {
    let l = lines[i].trim()
    if (!(/^(✕|✗|×|not ok\b|AssertionError|●)/.test(l) && l.length > 3)) continue
    l = l.replace(/^(✕|✗|×|not ok\s+\d*\s*-?)\s*/, '').replace(/^AssertionError(\s*\[[A-Z_]+\])?:\s*/, '')
    // Satır iki nokta ile bitiyorsa asıl bilgi sonraki dolu satırdadır (ör. "1 !== 2").
    if (/:$/.test(l)) {
      const nx = lines.slice(i + 1).map((x) => x.trim()).find((x) => x.length > 0)
      if (nx) l = l + ' ' + nx
    }
    return l.slice(0, 140)
  }
  return null
}


import { app } from '../store.svelte'
import { en } from './en'
import { tr, type Key } from './tr'

export type { Key }

/** Metni seçili dilde verir; {ad} yer tutucularını doldurur. */
export function t(key: Key, params?: Record<string, string | number>): string {
  const dict: Partial<Record<Key, string>> = app.lang === 'en' ? en : tr
  let s: string = dict[key] ?? tr[key] ?? key
  if (params) for (const [k, v] of Object.entries(params)) s = s.replaceAll(`{${k}}`, String(v))
  return s
}

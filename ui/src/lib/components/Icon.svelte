<script module lang="ts">
  export type IconName =
    | 'toparla' | 'defter' | 'tekrarlar' | 'ara' | 'karne' | 'ayarlar'
    | 'waiting' | 'stalled' | 'working' | 'idle'
    | 'question' | 'alert' | 'check' | 'x' | 'pin' | 'edit' | 'archive' | 'undo'
    | 'isler' | 'bekci' | 'harcama' | 'send' | 'play' | 'terminal' | 'phone' | 'moon' | 'keyboard' | 'shield' | 'unchecked' | 'refresh' | 'clock' | 'image' | 'trash' | 'back'
    | 'copy' | 'plus' | 'chevron' | 'explain' | 'globe' | 'download' | 'layers' | 'lock' | 'folder' | 'mic' | 'stop' | 'command' | 'inbox' | 'person' | 'info' | 'more' | 'sun' | 'spark'
</script>

<script lang="ts">
  let { name, size = 20, strokeWidth = 1.7 }: { name: IconName; size?: number; strokeWidth?: number } = $props()
</script>

<svg
  width={size}
  height={size}
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width={strokeWidth}
  stroke-linecap="round"
  stroke-linejoin="round"
  aria-hidden="true"
  focusable="false"
>
  {#if name === 'more'}
    <circle cx="5.5" cy="12" r="1.4" />
    <circle cx="12" cy="12" r="1.4" />
    <circle cx="18.5" cy="12" r="1.4" />
  {:else if name === 'toparla'}
    <path d="M4 7h10M4 12h16M4 17h7" />
    <circle cx="18" cy="7" r="1.6" />
  {:else if name === 'defter'}
    <path d="M6 4h11a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H6z" />
    <path d="M6 4v16M10 9h5M10 13h4" />
  {:else if name === 'tekrarlar'}
    <path d="M5 11V9a3 3 0 0 1 3-3h9" />
    <path d="m14 3 3 3-3 3" />
    <path d="M19 13v2a3 3 0 0 1-3 3H7" />
    <path d="m10 21-3-3 3-3" />
  {:else if name === 'ara'}
    <circle cx="11" cy="11" r="6" />
    <path d="m20 20-4.2-4.2" />
  {:else if name === 'karne'}
    <path d="M5 20V12M10 20V6M15 20v-9M20 20V9" />
  {:else if name === 'ayarlar'}
    <path d="M4 7h9M17 7h3M4 17h3M11 17h9" />
    <circle cx="15" cy="7" r="2" />
    <circle cx="9" cy="17" r="2" />
  {:else if name === 'waiting'}
    <path d="M12 3a5 5 0 0 0-5 5v3.5L5.5 15h13L17 11.5V8a5 5 0 0 0-5-5z" />
    <path d="M10 18.5a2 2 0 0 0 4 0" />
  {:else if name === 'stalled'}
    <path d="M12 4 3.5 19h17z" />
    <path d="M12 10v4M12 16.6v.1" />
  {:else if name === 'working'}
    <path d="M3 12h3.5l2.2-6 4 12 2.3-6H21" />
  {:else if name === 'idle'}
    <path d="M9 6v12M15 6v12" />
  {:else if name === 'question'}
    <circle cx="12" cy="12" r="8.5" />
    <path d="M9.6 9.6a2.5 2.5 0 1 1 3.4 2.3c-.6.3-1 .8-1 1.5M12 16.6v.1" />
  {:else if name === 'alert'}
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 7.5v5.2M12 16.2v.1" />
  {:else if name === 'check'}
    <path d="m5 12.5 4.5 4.5L19 7.5" />
  {:else if name === 'x'}
    <path d="M6 6l12 12M18 6 6 18" />
  {:else if name === 'pin'}
    <path d="M14.5 4.5 19.5 9.5l-2.2.8-2.8 2.8.3 3.4-1.2 1.2-3.4-3.4-4.6 4.6M9.7 8.7l-1 1.2-1.2-1.2 1.2-1.2z" />
    <path d="m13.8 5.2-3.6 3.6" />
  {:else if name === 'edit'}
    <path d="M5 19h3.5L19 8.5 15.5 5 5 15.5z" />
    <path d="m13.5 7 3.5 3.5" />
  {:else if name === 'archive'}
    <path d="M4 6h16v3H4zM5.5 9v9a1 1 0 0 0 1 1h11a1 1 0 0 0 1-1V9M10 13h4" />
  {:else if name === 'undo'}
    <path d="M9 7 4.5 11.5 9 16" />
    <path d="M5 11.5h9a5 5 0 0 1 0 10" transform="translate(0 -3.5)" />
  {:else if name === 'copy'}
    <rect x="8.5" y="8.5" width="11" height="11" rx="2" />
    <path d="M15.5 8.5V6.5a2 2 0 0 0-2-2h-7a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h2" />
  {:else if name === 'plus'}
    <path d="M12 5v14M5 12h14" />
  {:else if name === 'chevron'}
    <path d="m7 10 5 5 5-5" />
  {:else if name === 'explain'}
    <path d="M5 5h14v10H11l-4 3.5V15H5z" />
    <path d="M9 9h6M9 12h3.5" />
  {:else if name === 'globe'}
    <circle cx="12" cy="12" r="8.5" />
    <path d="M3.5 12h17M12 3.5c2.5 2.5 3.5 5.4 3.5 8.5s-1 6-3.5 8.5c-2.5-2.5-3.5-5.4-3.5-8.5s1-6 3.5-8.5z" />
  {:else if name === 'download'}
    <path d="M12 4v11M7.5 11 12 15.5 16.5 11M5 19.5h14" />
  {:else if name === 'layers'}
    <path d="m12 4 8.5 4.5L12 13 3.5 8.5z" />
    <path d="m3.5 12.5 8.5 4.5 8.5-4.5" />
  {:else if name === 'lock'}
    <rect x="5" y="10.5" width="14" height="9" rx="2" />
    <path d="M8 10.5V8a4 4 0 0 1 8 0v2.5" />
  {:else if name === 'isler'}
    <path d="M9 6.5h10M9 12h10M9 17.5h10" />
    <path d="m3.8 6.4 1.2 1.2 2-2.2M3.8 11.9l1.2 1.2 2-2.2M3.8 17.4l1.2 1.2 2-2.2" />
  {:else if name === 'bekci'}
    <path d="M12 3.5 5 6v5.5c0 4.2 2.8 7.3 7 9 4.2-1.7 7-4.8 7-9V6z" />
    <path d="m9 12 2.2 2.2L15.4 10" />
  {:else if name === 'harcama'}
    <circle cx="12" cy="12" r="8.5" />
    <path d="M14.6 9.2c-.5-.8-1.4-1.2-2.6-1.2-1.5 0-2.5.8-2.5 1.9 0 2.7 5.2 1.2 5.2 4 0 1.1-1.1 2-2.7 2-1.3 0-2.3-.5-2.9-1.4M12 6.6V8M12 16v1.4" />
  {:else if name === 'send'}
    <path d="M4 12 20 4.5 15 20l-3.6-6.2z" />
    <path d="m11.4 13.8 3.6-4" />
  {:else if name === 'play'}
    <path d="M8 5.5v13l10.5-6.5z" />
  {:else if name === 'terminal'}
    <rect x="3.5" y="5" width="17" height="14" rx="2.5" />
    <path d="m7.5 10 3 2.5-3 2.5M13 15h3.5" />
  {:else if name === 'mic'}
    <rect x="9" y="3.5" width="6" height="11" rx="3" />
    <path d="M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v2.5" />
  {:else if name === 'phone'}
    <rect x="7" y="3.5" width="10" height="17" rx="2.5" />
    <path d="M11 17.5h2" />
  {:else if name === 'moon'}
    <path d="M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z" />
  {:else if name === 'keyboard'}
    <rect x="3" y="6.5" width="18" height="11" rx="2.5" />
    <path d="M7 10.5h.1M10.5 10.5h.1M14 10.5h.1M17 10.5h.1M8 14h8" />
  {:else if name === 'shield'}
    <path d="M12 3.5 5 6v5.5c0 4.2 2.8 7.3 7 9 4.2-1.7 7-4.8 7-9V6z" />
  {:else if name === 'unchecked'}
    <circle cx="12" cy="12" r="8.5" stroke-dasharray="3 2.6" />
    <path d="M8.8 12h6.4" />
  {:else if name === 'refresh'}
    <path d="M19.5 12a7.5 7.5 0 0 1-13 5.1M4.5 12a7.5 7.5 0 0 1 13-5.1" />
    <path d="M17.5 3.5v3.6h-3.6M6.5 20.5v-3.6h3.6" />
  {:else if name === 'clock'}
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 7.5V12l3 2" />
  {:else if name === 'image'}
    <rect x="3.5" y="5" width="17" height="14" rx="2.5" />
    <path d="m4 16 4.5-4.5 3.5 3.5 2.5-2.5L20 17M15.5 9.5h.1" />
  {:else if name === 'trash'}
    <path d="M5 7h14M10 7V5h4v2M7 7l.8 12h8.4L17 7M10.5 11v5M13.5 11v5" />
  {:else if name === 'back'}
    <path d="M10 6 4.5 12 10 18M5 12h14.5" />
  {:else if name === 'folder'}
    <path d="M3.5 7.5a1.5 1.5 0 0 1 1.5-1.5h4l2 2.2h8a1.5 1.5 0 0 1 1.5 1.5v8.3a1.5 1.5 0 0 1-1.5 1.5H5a1.5 1.5 0 0 1-1.5-1.5z" />
  {:else if name === 'stop'}
    <rect x="6.5" y="6.5" width="11" height="11" rx="2.2" />
  {:else if name === 'command'}
    <path d="M9 9V6.5A2.5 2.5 0 1 0 6.5 9H9zm0 0h6m-6 0v6m6-6V6.5A2.5 2.5 0 1 1 17.5 9H15zm0 0v6m0 0h2.5a2.5 2.5 0 1 1-2.5 2.5V15zm0 0H9m0 0H6.5A2.5 2.5 0 1 0 9 17.5V15z" />
  {:else if name === 'inbox'}
    <path d="M4 13.5 6.5 6h11L20 13.5V18a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1z" />
    <path d="M4 13.5h4.2l1 2h5.6l1-2H20" />
  {:else if name === 'person'}
    <circle cx="10" cy="8.5" r="3.2" />
    <path d="M4 19.5a6 6 0 0 1 9.6-4.8M15 17.5l2 2 3.5-4" />
  {:else if name === 'sun'}
    <circle cx="12" cy="12" r="4" />
    <path d="M12 3.5v2M12 18.5v2M3.5 12h2M18.5 12h2M6 6l1.4 1.4M16.6 16.6 18 18M18 6l-1.4 1.4M7.4 16.6 6 18" />
  {:else if name === 'spark'}
    <path d="M12 3.5 13.9 9l5.6 1.9-5.6 1.9L12 18.5l-1.9-5.7-5.6-1.9L10.1 9z" />
    <path d="M18.5 3.5v3M17 5h3" />
  {:else if name === 'info'}
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 11v5M12 7.9h.01" />
  {/if}
</svg>

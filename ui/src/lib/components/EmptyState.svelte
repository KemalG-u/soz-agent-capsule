<script lang="ts">
  // Boş durum kalıbı: tek cümle ve tek düğme. Bütün sayfalarda aynıdır.
  let { text, action, onaction, id }: { text: string; action?: string; onaction?: () => void; id?: string } = $props()
</script>

<div class="empty-state" data-empty={id}>
  <p>{text}</p>
  {#if action && onaction}<button class="btn btn-sm" onclick={onaction}>{action}</button>{/if}
</div>

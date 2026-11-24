<script lang="ts">
  // Yer tutucu iskelet: yükleme sırasında sayfa sıçramasın. Hareketsizdir (yanıp sönme yok).
  let { rows = 3 }: { rows?: number } = $props()
</script>

<div class="sk" role="status" aria-label="Yükleniyor">
  {#each Array(rows) as _, i (i)}
    <div class="skrow">
      <span class="skel" style="width: {38 + ((i * 17) % 30)}%"></span>
      <span class="skel" style="width: {72 - ((i * 11) % 24)}%; height: 0.9em"></span>
    </div>
  {/each}
</div>

<style>
  .sk { display: flex; flex-direction: column; }
  .skrow {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 22px 0 22px 22px;
    border-top: 1px solid var(--line);
  }
</style>

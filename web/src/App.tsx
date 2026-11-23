import { useCallback, useEffect, useMemo, useRef, useState } from 'preact/hooks';
import { CUSTOM_MAX_MB, CUSTOM_MIN_MB, DEFAULT_MB, Presets, toBytes } from './core/tuning';
import type { Probed } from './engine/compress';
import { mb } from './engine/format';

// The engines (Mediabunny, gifenc) load on first use, so the page itself opens fast on a phone.
const engine = () => import('./engine/compress');
import { SquishError, type FormatPref, type JobResult } from './engine/types';

type State = 'probing' | 'queued' | 'working' | 'done' | 'fits' | 'failed';

type Item = {
  id: number;
  file: File;
  state: State;
  detail: string;
  prediction: string;
  warning?: string;
  status: string;
  progress: number;
  result?: JobResult;
  thumb?: string;
  isVideo: boolean;
  kind?: Probed['kind'];
  targetMb?: number;
  format: FormatPref;
  inRun: boolean;
};

const DISCORD_ATTACHMENT_LIMIT = 10;
const store = {
  get: (k: string) => { try { return localStorage.getItem(k); } catch { return null; } },
  set: (k: string, v: string) => { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
};

let nextId = 1;

export function App() {
  const [items, setItems] = useState<Item[]>([]);
  const [targetMb, setTargetMb] = useState<number>(() => Number(store.get('squish.targetMb')) || DEFAULT_MB);
  const [imageFormat, setImageFormat] = useState<FormatPref>(() => (store.get('squish.imageFormat') as FormatPref) || 'auto');
  const [running, setRunning] = useState(false);
  const [batchDone, setBatchDone] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [customOpen, setCustomOpen] = useState(false);
  const [announce, setAnnounce] = useState('');
  const [dragging, setDragging] = useState(false);
  const probed = useRef(new Map<number, Probed>());
  const controllers = useRef(new Map<number, AbortController>());
  const fileInput = useRef<HTMLInputElement>(null);
  const runningRef = useRef(false);
  const support = useMemo(detectSupport, []);

  useEffect(() => store.set('squish.targetMb', String(targetMb)), [targetMb]);
  useEffect(() => store.set('squish.imageFormat', imageFormat), [imageFormat]);

  const patch = useCallback((id: number, p: Partial<Item>) => setItems((list) => list.map((i) => (i.id === id ? { ...i, ...p } : i))), []);

  const targetFor = (i: Item) => toBytes(i.targetMb ?? targetMb);

  // ---------------- Adding files

  const addFiles = useCallback((files: Iterable<File>) => {
    const added: Item[] = [];
    for (const file of files) {
      const isVideo = file.type.startsWith('video/');
      const thumb = file.type.startsWith('image/') || isVideo ? URL.createObjectURL(file) : undefined;
      added.push({ id: nextId++, file, state: 'probing', detail: 'Reading…', prediction: '', status: '', progress: 0, thumb, isVideo, format: 'auto', inRun: false });
    }
    if (!added.length) return;
    setBatchDone(false);
    setItems((list) => [...list, ...added]);
    for (const item of added) {
      engine().then((e) => e.probe(item.file))
        .then((p) => {
          probed.current.set(item.id, p);
          patch(item.id, { state: 'queued', detail: p.detail, kind: p.kind, isVideo: p.kind === 'video' });
        })
        .catch((e) => patch(item.id, { state: 'failed', detail: mb(item.file.size), status: message(e) }));
    }
  }, [patch]);

  // Predictions follow the target, per-file overrides and format.
  const [predictFn, setPredictFn] = useState<typeof import('./engine/compress').predict | null>(null);
  useEffect(() => { if (items.length && !predictFn) engine().then((e) => setPredictFn(() => e.predict)); }, [items.length, predictFn]);
  useEffect(() => {
    if (!predictFn) return;
    setItems((list) => list.map((i) => {
      const p = probed.current.get(i.id);
      if (!p || i.state !== 'queued') return i;
      const pr = predictFn(p, i.file, targetFor(i));
      return pr.line === i.prediction && pr.warning === i.warning ? i : { ...i, prediction: pr.line, warning: pr.warning };
    }));
  });

  useEffect(() => {
    const onPaste = (e: ClipboardEvent) => {
      const files = e.clipboardData?.files;
      if (files?.length) { e.preventDefault(); addFiles(files); }
    };
    window.addEventListener('paste', onPaste);
    return () => window.removeEventListener('paste', onPaste);
  }, [addFiles]);

  // Dev-only hook so automated browser tests can feed files without a file picker.
  useEffect(() => {
    if (!import.meta.env.DEV) return;
    (window as unknown as { __squishItems: () => Item[] }).__squishItems = () => itemsRef.current;
    (window as unknown as { __squishAddUrls: (u: string[]) => Promise<void> }).__squishAddUrls = async (urls) => {
      const files = await Promise.all(urls.map(async (u) => {
        const b = await (await fetch(u)).blob();
        return new File([b], decodeURIComponent(u.split('/').pop()!), { type: b.type });
      }));
      addFiles(files);
    };
  }, [addFiles]);

  // ---------------- Running (one file at a time: phones have little memory)

  // The queue of ids is the loop's source of truth; itemsRef gives it the latest file, size and format choices.
  const queue = useRef<number[]>([]);
  const itemsRef = useRef(items);
  itemsRef.current = items;

  const run = async (ids: number[]) => {
    const fresh = ids.filter((id) => !queue.current.includes(id) && !controllers.current.has(id));
    if (!fresh.length) return;
    for (const id of fresh) { queue.current.push(id); controllers.current.set(id, new AbortController()); }
    setItems((list) => list.map((i) => (fresh.includes(i.id) ? { ...i, inRun: true, state: 'queued', status: 'Waiting…', progress: 0 } : i)));
    if (runningRef.current) return; // a Retry while running joins the active loop
    runningRef.current = true;
    setRunning(true);
    setBatchDone(false);
    const lock = await wakeLock();
    try {
      for (let id = queue.current.shift(); id !== undefined; id = queue.current.shift()) {
        const item = itemsRef.current.find((i) => i.id === id);
        const ctrl = controllers.current.get(id);
        if (!item || !ctrl || ctrl.signal.aborted) {
          controllers.current.delete(id);
          patch(id, { inRun: false, state: 'queued', status: ctrl?.signal.aborted ? 'Cancelled' : '' });
          continue;
        }
        await runOne(item, ctrl);
      }
    } finally {
      lock?.release().catch(() => {});
      runningRef.current = false;
      setRunning(false);
      setBatchDone(true);
      setTimeout(() => {
        const list = itemsRef.current;
        const ok = list.filter((i) => i.result).length, failed = list.filter((i) => i.state === 'failed').length;
        setAnnounce(failed ? `Finished. ${ok} ready, ${failed} failed.` : `Finished. ${ok} ready to share.`);
      }, 0);
    }
  };

  async function runOne(item: Item, ctrl: AbortController) {
    let p = probed.current.get(item.id);
    try {
      const { probe, compress } = await engine();
      if (!p) { p = await probe(item.file); probed.current.set(item.id, p); }
      patch(item.id, { state: 'working', status: 'Starting', progress: 0 });
      let last = 0;
      const result = await compress({
        file: item.file, targetBytes: targetFor(item), signal: ctrl.signal,
        format: item.format !== 'auto' ? item.format : p.kind === 'image' ? imageFormat : 'auto',
        onProgress: (f, step) => {
          const now = performance.now();
          if (now - last < 120 && f < 1) return; // keep re-renders cheap on phones
          last = now;
          patch(item.id, { progress: f, status: `${step} · ${Math.round(f * 100)}%` });
        },
      }, p);
      patch(item.id, {
        state: result.alreadyFit ? 'fits' : 'done', result, inRun: false, progress: 1, warning: undefined,
        status: result.alreadyFit ? 'No compression needed' : `${mb(item.file.size)} → ${mb(result.blob.size)}`,
        prediction: result.alreadyFit ? '' : result.warnings[0] ?? result.summary,
      });
      setAnnounce(`${item.file.name} done.`);
    } catch (e) {
      const cancelledRun = e instanceof SquishError && e.code === 'cancelled';
      patch(item.id, cancelledRun
        ? { state: 'queued', inRun: false, status: 'Cancelled', progress: 0 }
        : { state: 'failed', inRun: false, status: message(e), progress: 0 });
      if (!cancelledRun) console.error(e);
    } finally {
      controllers.current.delete(item.id);
    }
  }

  const cancel = (id: number) => controllers.current.get(id)?.abort();
  const remove = (id: number) => setItems((list) => {
    const i = list.find((x) => x.id === id);
    if (!i || i.inRun) return list;
    if (i.thumb) URL.revokeObjectURL(i.thumb);
    probed.current.delete(id);
    return list.filter((x) => x.id !== id);
  });
  const clear = () => { if (!running) { items.forEach((i) => i.thumb && URL.revokeObjectURL(i.thumb)); probed.current.clear(); setItems([]); setBatchDone(false); } };

  const runnable = items.filter((i) => i.state === 'queued' || (i.state === 'failed' && probed.current.has(i.id)));
  const finished = items.filter((i) => i.result);

  const primary = async () => {
    if (running) return;
    if (batchDone && finished.length && !runnable.some((i) => i.state === 'queued')) { await shareFiles(finished.map((i) => toFile(i.result!))); return; }
    run(runnable.map((i) => i.id));
  };

  const primaryText = running ? 'Compressing…'
    : batchDone && finished.length && !runnable.some((i) => i.state === 'queued') ? (finished.length === 1 ? 'Share' : `Share all (${finished.length})`)
    : runnable.length === 1 ? 'Compress 1 file' : `Compress ${runnable.length} files`;

  const preset = Presets.find((p) => Math.abs(p.megabytes - targetMb) < 0.001);

  return (
    <div class={`app ${dragging ? 'dragging' : ''}`}
      onDragOver={(e) => { if (e.dataTransfer?.types.includes('Files')) { e.preventDefault(); setDragging(true); } }}
      onDragLeave={(e) => { if (e.currentTarget === e.target) setDragging(false); }}
      onDrop={(e) => { e.preventDefault(); setDragging(false); if (e.dataTransfer?.files.length) addFiles(e.dataTransfer.files); }}>

      <header class="top">
        <div class="brand"><img src={`${import.meta.env.BASE_URL}icon.svg`} alt="" width="22" height="22" /><span>Squish</span></div>
        <button class="icon-btn" aria-label="Settings" onClick={() => setSettingsOpen(true)}><Icon name="settings" /></button>
      </header>

      <section class="target" aria-label="Target size">
        <div class="segments" role="radiogroup" aria-label="Target size in megabytes">
          {Presets.map((p) => (
            <button role="radio" aria-checked={preset?.id === p.id} class={preset?.id === p.id ? 'on' : ''}
              aria-label={`${p.label} megabytes, ${p.caption}`} onClick={() => setTargetMb(p.megabytes)}>{p.label}</button>
          ))}
          <button role="radio" aria-checked={!preset} class={!preset ? 'on' : ''} onClick={() => setCustomOpen(true)}>
            {preset ? 'Custom' : `${round1(targetMb)}`}
          </button>
        </div>
        <p class="caption" aria-live="polite">{preset ? `${preset.caption} · ${preset.label} MB` : `Custom · ${round1(targetMb)} MB`}</p>
      </section>

      <main class="list">
        {!support.video && <p class="banner">This browser can't compress videos (no WebCodecs). Images still work. Try Chrome, or Safari on iOS 17 or later.</p>}
        {items.length === 0 ? (
          <div class="empty">
            <div class="empty-glyph" aria-hidden="true"><Icon name="squish" /></div>
            <p class="empty-title">Shrink videos, images and GIFs to fit Discord</p>
            <p class="caption">Everything happens on this device. Nothing is uploaded.</p>
            <button class="btn accent big" onClick={() => fileInput.current?.click()}>Choose files</button>
          </div>
        ) : (
          <ul class="cards" aria-label="Files">
            {items.map((i) => (
              <Card key={i.id} item={i} running={running}
                onRemove={() => remove(i.id)} onCancel={() => cancel(i.id)} onRetry={() => run([i.id])}
                onShare={() => i.result && shareFiles([toFile(i.result)])} onSave={() => i.result && save(i.result)}
                onTarget={(m) => patch(i.id, { targetMb: m })} onFormat={(f) => patch(i.id, { format: f })} />
            ))}
          </ul>
        )}
      </main>

      {items.length > 0 && <footer class="actions">
        {batchDone && finished.length > DISCORD_ATTACHMENT_LIMIT && <p class="caption">Discord takes {DISCORD_ATTACHMENT_LIMIT} files per message. Share in batches.</p>}
        <div class="action-row">
          <button class="btn" onClick={() => fileInput.current?.click()} aria-label="Add files"><Icon name="plus" /></button>
          <button class="btn accent grow" disabled={!items.length || (running) || (!runnable.length && !finished.length)} onClick={primary}>{primaryText}</button>
          <button class="btn" disabled={running} onClick={clear}>Clear</button>
        </div>
      </footer>}

      <input ref={fileInput} type="file" multiple accept="video/*,image/*" hidden
        onChange={(e) => { const f = (e.currentTarget as HTMLInputElement).files; if (f) addFiles(Array.from(f)); (e.currentTarget as HTMLInputElement).value = ''; }} />

      {customOpen && <CustomSize value={targetMb} onClose={() => setCustomOpen(false)} onSet={(v) => { setTargetMb(v); setCustomOpen(false); }} />}
      {settingsOpen && <Settings format={imageFormat} onFormat={setImageFormat} support={support} onClose={() => setSettingsOpen(false)} />}
      <div class="sr-only" aria-live="assertive">{announce}</div>
    </div>
  );
}

// ---------------- Card

function Card(props: {
  item: Item; running: boolean; onRemove: () => void; onCancel: () => void; onRetry: () => void;
  onShare: () => void; onSave: () => void; onTarget: (mb?: number) => void; onFormat: (f: FormatPref) => void;
}) {
  const { item: i } = props;
  const [menu, setMenu] = useState(false);
  const ok = i.state === 'done' || i.state === 'fits';
  return (
    <li class={`card ${i.state}`} aria-label={`${i.file.name}. ${i.state === 'done' ? 'Done. ' : i.state === 'failed' ? 'Failed. ' : ''}${i.status || i.prediction}`}>
      <div class="thumb">
        {i.thumb && (i.isVideo
          ? <video src={`${i.thumb}#t=0.5`} muted playsInline preload="metadata" />
          : <img src={i.thumb} alt="" onError={(e) => ((e.currentTarget as HTMLImageElement).style.display = 'none')} />)}
        {ok && <span class="badge ok"><Icon name="check" /></span>}
        {i.state === 'failed' && <span class="badge bad"><Icon name="error" /></span>}
      </div>
      <div class="body">
        <p class="name" title={i.file.name}>{i.file.name}</p>
        <p class="caption">{i.detail}</p>
        {i.state !== 'working' && i.prediction && <p class="caption">{i.prediction}{i.targetMb ? ` · ${round1(i.targetMb)} MB for this file` : ''}</p>}
        {i.warning && (i.state === 'queued' || i.state === 'probing') && <p class="chip warn"><Icon name="warn" />{i.warning}</p>}
        {i.state === 'working' && <div class="bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(i.progress * 100)}><div style={{ width: `${i.progress * 100}%` }} /></div>}
        {i.status && <p class={`status ${ok ? 'ok' : i.state === 'failed' ? 'bad' : ''}`}>{i.status}</p>}
        {ok && (
          <div class="card-actions">
            <button class="btn small accent" onClick={props.onShare}><Icon name="share" />Share</button>
            <button class="btn small" onClick={props.onSave}><Icon name="save" />Save</button>
          </div>
        )}
      </div>
      <div class="side">
        {(i.state === 'working' || i.inRun) && <button class="icon-btn" aria-label="Cancel" onClick={props.onCancel}><Icon name="close" /></button>}
        {i.state === 'failed' && !i.inRun && i.kind && <button class="icon-btn" aria-label="Retry" onClick={props.onRetry}><Icon name="retry" /></button>}
        {i.state === 'queued' && !i.inRun && (
          <button class="icon-btn" aria-label="More options" aria-expanded={menu} onClick={() => setMenu(!menu)}><Icon name="more" /></button>
        )}
        {!i.inRun && i.state !== 'working' && <button class="icon-btn" aria-label="Remove" onClick={props.onRemove}><Icon name="close" /></button>}
      </div>
      {menu && (
        <div class="menu" role="menu">
          <p class="menu-title">Size for this file</p>
          <div class="menu-row">
            <button role="menuitem" class={!i.targetMb ? 'on' : ''} onClick={() => { props.onTarget(undefined); setMenu(false); }}>Same</button>
            {Presets.map((p) => <button role="menuitem" class={i.targetMb === p.megabytes ? 'on' : ''} onClick={() => { props.onTarget(p.megabytes); setMenu(false); }}>{p.label}</button>)}
          </div>
          {i.kind === 'image' && <>
            <p class="menu-title">Format</p>
            <div class="menu-row">
              {(['auto', 'jpeg', 'webp', 'png'] as FormatPref[]).map((f) => <button role="menuitem" class={i.format === f ? 'on' : ''} onClick={() => { props.onFormat(f); setMenu(false); }}>{formatLabel(f)}</button>)}
            </div>
          </>}
          {i.kind === 'animated' && <>
            <p class="menu-title">Format</p>
            <div class="menu-row">
              <button role="menuitem" class={i.format !== 'video' ? 'on' : ''} onClick={() => { props.onFormat('auto'); setMenu(false); }}>Keep GIF</button>
              <button role="menuitem" class={i.format === 'video' ? 'on' : ''} onClick={() => { props.onFormat('video'); setMenu(false); }}>Convert to video</button>
            </div>
          </>}
        </div>
      )}
    </li>
  );
}

// ---------------- Sheets

function CustomSize(props: { value: number; onSet: (v: number) => void; onClose: () => void }) {
  const [v, setV] = useState(String(round1(props.value)));
  const n = Number(v);
  const valid = Number.isFinite(n) && n >= CUSTOM_MIN_MB && n <= CUSTOM_MAX_MB;
  return (
    <Sheet title="Custom size" onClose={props.onClose}>
      <form onSubmit={(e) => { e.preventDefault(); if (valid) props.onSet(Math.round(n * 10) / 10); }}>
        <label class="field">
          <span class="caption">Size in MB ({CUSTOM_MIN_MB}–{CUSTOM_MAX_MB})</span>
          <input type="number" inputMode="decimal" min={CUSTOM_MIN_MB} max={CUSTOM_MAX_MB} step="0.1" value={v} autoFocus
            onInput={(e) => setV((e.currentTarget as HTMLInputElement).value)} />
        </label>
        <button class="btn accent grow" type="submit" disabled={!valid}>Use {valid ? `${round1(n)} MB` : 'this size'}</button>
      </form>
    </Sheet>
  );
}

function Settings(props: { format: FormatPref; onFormat: (f: FormatPref) => void; support: Support; onClose: () => void }) {
  return (
    <Sheet title="Settings" onClose={props.onClose}>
      <p class="caption">Image format</p>
      <div class="segments">
        {(['auto', 'jpeg', 'webp', 'png'] as FormatPref[]).map((f) => (
          <button class={props.format === f ? 'on' : ''} aria-pressed={props.format === f} onClick={() => props.onFormat(f)}>{formatLabel(f)}</button>
        ))}
      </div>
      <p class="caption">Videos are saved as MP4 (H.264), which plays everywhere in Discord.</p>
      <h3>This device</h3>
      <ul class="support">
        <li class={props.support.video ? 'yes' : 'no'}>Video compression {props.support.video ? 'available' : 'not available in this browser'}</li>
        <li class={props.support.gif ? 'yes' : 'no'}>GIF compression {props.support.gif ? 'available' : 'not available in this browser'}</li>
        <li class={props.support.share ? 'yes' : 'no'}>Share to apps {props.support.share ? 'available' : 'not available; use Save'}</li>
      </ul>
      <h3>About</h3>
      <p class="caption">Squish works offline. Files never leave your device. Location data is removed from photos and videos it compresses. Add it to your home screen to use it like an app.</p>
      <p class="caption">Free software (GPL-3.0). Uses Mediabunny, gifenc and Preact.</p>
    </Sheet>
  );
}

function Sheet(props: { title: string; onClose: () => void; children: preact.ComponentChildren }) {
  useEffect(() => {
    const k = (e: KeyboardEvent) => e.key === 'Escape' && props.onClose();
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, [props.onClose]);
  return (
    <div class="scrim" onClick={(e) => e.target === e.currentTarget && props.onClose()}>
      <div class="sheet" role="dialog" aria-modal="true" aria-label={props.title}>
        <div class="sheet-head">
          <h2>{props.title}</h2>
          <button class="icon-btn" aria-label="Close" onClick={props.onClose}><Icon name="close" /></button>
        </div>
        {props.children}
      </div>
    </div>
  );
}

// ---------------- Helpers

type Support = { video: boolean; gif: boolean; share: boolean };
function detectSupport(): Support {
  return {
    video: typeof VideoEncoder !== 'undefined' && typeof VideoDecoder !== 'undefined',
    gif: typeof ImageDecoder !== 'undefined',
    share: typeof navigator.share === 'function' && typeof navigator.canShare === 'function',
  };
}

function toFile(r: JobResult): File {
  return r.blob instanceof File && r.blob.name === r.name ? r.blob : new File([r.blob], r.name, { type: r.blob.type });
}

/** The phone's share sheet: pick Discord, and the files attach to a message. Falls back to saving. */
async function shareFiles(files: File[]) {
  if (navigator.canShare?.({ files })) {
    try { await navigator.share({ files }); return; }
    catch (e) { if ((e as DOMException).name === 'AbortError') return; }
  }
  files.forEach((f) => save({ blob: f, name: f.name }));
}

function save(r: Pick<JobResult, 'blob' | 'name'>) {
  const url = URL.createObjectURL(r.blob);
  const a = Object.assign(document.createElement('a'), { href: url, download: r.name });
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

/** Phones suspend background tabs; keep the screen on while compressing. */
async function wakeLock(): Promise<WakeLockSentinel | null> {
  try { return (await navigator.wakeLock?.request('screen')) ?? null; } catch { return null; }
}

function message(e: unknown): string {
  if (e instanceof SquishError) return e.message;
  return 'Something went wrong. Try again, or try a different browser.';
}

const round1 = (n: number) => Math.round(n * 10) / 10;
const formatLabel = (f: FormatPref) => ({ auto: 'Auto', jpeg: 'JPEG', webp: 'WebP', png: 'PNG', video: 'Video' })[f];

function Icon({ name }: { name: string }) {
  const p: Record<string, string> = {
    settings: 'M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0M14 4v4M8 10v4M16 16v4',
    plus: 'M12 5v14M5 12h14',
    close: 'M6 6l12 12M18 6 6 18',
    check: 'M5 12.5l4.5 4.5L19 7.5',
    error: 'M12 7v6m0 4h.01',
    warn: 'M12 9v4m0 4h.01M10.3 3.9 2.4 18a2 2 0 0 0 1.7 3h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z',
    retry: 'M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7',
    more: 'M5 12h.01M12 12h.01M19 12h.01',
    share: 'M12 15V3m0 0L8 7m4-4 4 4M5 13v6a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-6',
    save: 'M12 3v12m0 0-4-4m4 4 4-4M5 21h14',
    squish: 'M4 15c0-2.8 3.6-5 8-5s8 2.2 8 5-3.6 4-8 4-8-1.2-8-4Zm8-7V3m0 5-3-3m3 3 3-3',
  };
  return (
    <svg class="icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d={p[name]} />
    </svg>
  );
}

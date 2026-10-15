/**
 * Cuts director takes (tools/shoot.ps1) into finished videos: 16:9 (1920x1080) and 9:16 (1080x1920).
 *
 *   deno run -A tools/edit.ts --out data/director/final --use take1:1-6 --use take2:1 --use take1:8-10 [--base data/director]
 *
 * --use <take>:<scenes> picks scenes in order (numbers as in that take's timeline). For each scene it keeps
 * the moments someone is talking or acting (from timeline.txt), trims the dead air between them, and crops
 * the game's picture out of the screen recording. Video and sound are put on the same clock with the sync
 * flash (video) and audio.wav.start; subtitles are the spoken lines, styled like the game's.
 */

const args = [...Deno.args];
const opt = (name: string, all = false) => {
  const out: string[] = [];
  for (let i = args.indexOf(name); i >= 0; i = args.indexOf(name)) { out.push(args[i + 1]); args.splice(i, 2); if (!all) break; }
  return out;
};
const base = opt("--base")[0] ?? "data/director";
const outDir = opt("--out")[0] ?? `${base}/final`;
const uses = opt("--use", true);
const clipsTake = opt("--clips")[0]; // clips mode: every clip_start..clip_end of this take as its own video
if (uses.length === 0 && !clipsTake) throw new Error("--use take1:1-10, or --clips take1");

const DISPLAY_LATENCY = 0.033; // the flash reaches the screen about a frame after it's logged
const PAD_BEFORE = 0.15, PAD_AFTER = 0.3, MERGE_GAP = 0.35;

type Ev = { t: number; kind: string; f: string[] };
type Take = { dir: string; events: Ev[]; videos: { file: string; start: number; x: number; y: number; w: number; h: number; offset: number }[]; audioOffset: number };

async function run(cmd: string, a: string[], quiet = true): Promise<string> {
  const r = await new Deno.Command(cmd, { args: a, stdout: "piped", stderr: "piped" }).output();
  const text = new TextDecoder().decode(r.stdout) + new TextDecoder().decode(r.stderr);
  if (!r.success) throw new Error(`${cmd} failed: ${text.slice(-2000)}`);
  if (!quiet) console.log(text);
  return text;
}

/** Times (s) where the middle of the game picture turns white: the sync flashes. */
async function flashes(file: string, x: number, y: number, w: number, h: number): Promise<number[]> {
  const out = await run("ffmpeg", ["-hide_banner", "-i", file, "-vf",
    `crop=${Math.round(w / 3)}:${Math.round(h / 3)}:${x + Math.round(w / 3)}:${y + Math.round(h / 3)},scale=16:10,format=gray,signalstats,metadata=print:key=lavfi.signalstats.YAVG`,
    "-f", "null", "-"]);
  const times: number[] = [];
  let pts = 0, prevWhite = false;
  for (const line of out.split("\n")) {
    const p = /pts_time:([\d.]+)/.exec(line);
    if (p) pts = parseFloat(p[1]);
    const m = /YAVG=([\d.]+)/.exec(line);
    if (m) {
      const white = parseFloat(m[1]) > 240;
      if (white && !prevWhite) times.push(pts);
      prevWhite = white;
    }
  }
  return times;
}

async function loadTake(name: string): Promise<Take> {
  const dir = `${base}/${name}`;
  const events = Deno.readTextFileSync(`${dir}/timeline.txt`).split(/\r?\n/).filter((l) => l.trim())
    .map((l) => { const f = l.split("\t"); return { t: parseInt(f[0]) / 1000, kind: f[1], f: f.slice(2) }; });
  const claps = events.filter((e) => e.kind === "clap").map((e) => e.t);
  const videos: Take["videos"] = [];
  for (const l of Deno.readTextFileSync(`${dir}/watch.txt`).split(/\r?\n/)) {
    const f = l.split("\t");
    if (f[1] !== "video_start") continue;
    const start = parseInt(f[0]) / 1000, n = f[2];
    const [x, y, w, h] = f.slice(3, 7).map((v) => parseInt(v));
    const file = `${dir}/video${n}.mkv`;
    const fl = await flashes(file, x, y, w, h);
    const clap = claps.find((c) => c > start);
    if (fl.length === 0 || clap === undefined) throw new Error(`${file}: no sync flash`);
    const offset = fl[0] - clap - DISPLAY_LATENCY; // video time = wall + offset
    const check = fl.length > 1 ? claps.filter((c) => c > start)[1] : undefined;
    console.log(`${file}: flash at ${fl[0].toFixed(3)}s${check ? `, second flash off by ${((fl[1] - DISPLAY_LATENCY - (check + offset)) * 1000).toFixed(0)} ms` : ""}`);
    videos.push({ file, start, x, y, w, h, offset });
  }
  const audioStart = parseInt(Deno.readTextFileSync(`${dir}/audio.wav.start`).trim()) / 1000;
  return { dir, events, videos, audioOffset: -audioStart };
}

type Clip = { take: Take; video: Take["videos"][0]; from: number; to: number; lines: { from: number; to: number; who: string; text: string }[] };

/** The talking and acting moments of one scene, as wall-clock intervals with what's said in each. */
function sceneClips(take: Take, n: number): Clip[] {
  const ev = take.events;
  const s = ev.findIndex((e) => e.kind === "scene_start" && e.f[0] === String(n));
  const e = ev.findIndex((x, i) => i > s && x.kind === "scene_end" && x.f[0] === String(n));
  if (s < 0 || e < 0) throw new Error(`${take.dir}: no scene ${n}`);
  const inScene = ev.slice(s, e + 1);
  const keep: [number, number][] = [];
  const lines: Clip["lines"] = [];
  inScene.forEach((x, i) => {
    if (x.kind === "speak") {
      const on = inScene.slice(i + 1).find((y) => y.kind === "audio_on");
      const off = on && inScene.slice(inScene.indexOf(on) + 1).find((y) => y.kind === "audio_off");
      if (!on || !off) return;
      keep.push([on.t - PAD_BEFORE, off.t + PAD_AFTER]);
      lines.push({ from: on.t, to: off.t, who: x.f[0], text: x.f.slice(2).join(" ") });
    }
    if (x.kind === "shot" && x.f[0] === "wide") {
      const next = inScene.slice(i + 1).find((y) => y.kind === "shot" || y.kind === "scene_end");
      keep.push([x.t, (next?.t ?? x.t + 4) + PAD_AFTER]);
    }
  });
  keep.sort((a, b) => a[0] - b[0]);
  const merged: [number, number][] = [];
  for (const k of keep) {
    const last = merged[merged.length - 1];
    if (last && k[0] - last[1] < MERGE_GAP) last[1] = Math.max(last[1], k[1]);
    else merged.push([...k]);
  }
  return merged.map(([from, to]) => {
    const video = [...take.videos].reverse().find((v) => v.start < from)!;
    return { take, video, from, to, lines: lines.filter((l) => l.to > from && l.from < to) };
  });
}

// ---------------------------------------------------------------- subtitles

const clean = (t: string) => t.replace(/\[[^\]]*\]/g, "").replace(/\s+/g, " ").trim();
const ts = (t: number) => {
  const cs = Math.max(0, Math.round(t * 100));
  const h = Math.floor(cs / 360000), m = Math.floor(cs / 6000) % 60, s = Math.floor(cs / 100) % 60, c = cs % 100;
  return `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}.${String(c).padStart(2, "0")}`;
};

/** Split a line into chunks of at most `max` characters at word boundaries. */
function chunks(text: string, max: number): string[] {
  const out: string[] = [];
  let cur = "";
  for (const w of text.split(" ")) {
    if (cur && (cur + " " + w).length > max) { out.push(cur); cur = w; } else cur = cur ? cur + " " + w : w;
  }
  if (cur) out.push(cur);
  return out;
}

function ass(width: number, height: number, style: string, events: { from: number; to: number; text: string }[], max: number): string {
  const lines: string[] = [];
  for (const e of events) {
    const parts = chunks(e.text, max);
    const total = parts.reduce((n, p) => n + p.length, 0);
    let t = e.from;
    for (const p of parts) {
      const d = (e.to - e.from) * p.length / total;
      lines.push(`Dialogue: 0,${ts(t)},${ts(t + d)},Main,,0,0,0,,${p}`);
      t += d;
    }
  }
  return `[Script Info]
ScriptType: v4.00+
PlayResX: ${width}
PlayResY: ${height}
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: ${style}

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
${lines.join("\n")}
`;
}

// ---------------------------------------------------------------- clips mode

if (clipsTake) {
  const take = await loadTake(clipsTake);
  const dir = `${outDir}`;
  await Deno.mkdir(dir, { recursive: true });
  const starts = take.events.filter((e) => e.kind === "clip_start");
  for (const st of starts) {
    const n = st.f[0], title = st.f[1] ?? "";
    const end = take.events.find((e) => e.kind === "clip_end" && e.f[0] === n && e.t > st.t);
    if (!end) continue;
    // clip_end comes 0.6 s after the last word or move; 10 s at most (9.9 s of video: the audio track runs a frame
    // longer). A line that doesn't fit is reported, for a re-take.
    const lastWord = take.events.filter((e) => e.kind === "audio_off" && e.t > st.t && e.t < end.t).pop()?.t ?? st.t;
    const from = st.t - 0.2, dur = Math.min(9.9, end.t - 0.3 - from);
    if (lastWord + 0.1 > from + dur) console.log(`${title}: the last line ends ${(lastWord - from).toFixed(1)} s in, past the cut: re-take it`);
    const video = [...take.videos].reverse().find((v) => v.start < from)!;
    const slug = title.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
    const file = `${dir}/${String(n).padStart(2, "0")}-${slug}.mp4`;
    await run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y",
      "-ss", (from + video.offset).toFixed(3), "-t", dur.toFixed(3), "-i", video.file,
      "-ss", (from + take.audioOffset).toFixed(3), "-t", dur.toFixed(3), "-i", `${take.dir}/audio.wav`,
      "-map", "0:v", "-map", "1:a",
      "-vf", `crop=${video.w}:${video.h}:${video.x}:${video.y},scale=1920:-2:flags=lanczos,fps=30,setsar=1`,
      "-af", `loudnorm=I=-14:TP=-1.5:LRA=11,aresample=48000,afade=t=in:d=0.05,afade=t=out:st=${Math.max(0, dur - 0.15).toFixed(3)}:d=0.15`,
      "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
      "-c:a", "aac", "-b:a", "192k", file]);
    console.log(`${file}: ${dur.toFixed(1)}s`);
  }
  Deno.exit(0);
}

// ---------------------------------------------------------------- render

await Deno.mkdir(`${outDir}/clips`, { recursive: true });
const takes = new Map<string, Take>();
const clips: Clip[] = [];
for (const u of uses) {
  const [name, range] = u.split(":");
  if (!takes.has(name)) takes.set(name, await loadTake(name));
  for (const part of range.split(",")) {
    const [a, b] = part.split("-").map((v) => parseInt(v));
    for (let n = a; n <= (b ?? a); n++) clips.push(...sceneClips(takes.get(name)!, n));
  }
}

const subs: { from: number; to: number; text: string }[] = [];
const list: string[] = [];
let at = 0;
for (let i = 0; i < clips.length; i++) {
  const c = clips[i];
  const vFrom = c.from + c.video.offset, aFrom = c.from + c.take.audioOffset, dur = c.to - c.from;
  const file = `${outDir}/clips/clip${String(i).padStart(3, "0")}.mkv`;
  await run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y",
    "-ss", vFrom.toFixed(3), "-t", dur.toFixed(3), "-i", c.video.file,
    "-ss", aFrom.toFixed(3), "-t", dur.toFixed(3), "-i", `${c.take.dir}/audio.wav`,
    "-map", "0:v", "-map", "1:a",
    "-vf", `crop=${c.video.w}:${c.video.h}:${c.video.x}:${c.video.y},fps=30,setsar=1`,
    "-af", `aresample=48000,afade=t=in:d=0.02,afade=t=out:st=${Math.max(0, dur - 0.03).toFixed(3)}:d=0.03`,
    "-c:v", "libx264", "-preset", "veryfast", "-crf", "12", "-pix_fmt", "yuv420p",
    "-c:a", "pcm_s16le", "-ac", "2", file]);
  list.push(`file '${file.split("/").pop()}'`);
  for (const l of c.lines) {
    const text = clean(l.text);
    if (text) subs.push({ from: at + Math.max(0, l.from - c.from), to: at + Math.min(dur, l.to - c.from), text });
  }
  at += dur;
  console.log(`clip ${i}: ${c.take.dir.split("/").pop()} ${dur.toFixed(1)}s ${c.lines.map((l) => l.who).join(", ")}`);
}
Deno.writeTextFileSync(`${outDir}/clips/list.txt`, list.join("\n") + "\n");
console.log(`${clips.length} clips, ${at.toFixed(1)}s`);

// Game-style subtitles: white, black outline, a soft shadow.
Deno.writeTextFileSync(`${outDir}/subs_16x9.ass`, ass(1920, 1080,
  "Main,Segoe UI Semibold,46,&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,160,160,70,1", subs, 64));
Deno.writeTextFileSync(`${outDir}/subs_9x16.ass`, ass(1080, 1920,
  "Main,Segoe UI Black,64,&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,5,2,2,70,70,520,1", subs, 22));

const concat = ["-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", `${outDir}/clips/list.txt`];
const audio = ["-af", "loudnorm=I=-14:TP=-1.5:LRA=11,aresample=48000", "-c:a", "aac", "-b:a", "192k"];
const video = ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-r", "30", "-movflags", "+faststart"];
const esc = (p: string) => p.replace(/\\/g, "/").replace(/:/g, "\\:");

// 16:9: the game picture is 16:10; trim a strip off the top and bottom.
await run("ffmpeg", [...concat, "-vf",
  `crop=iw:iw*9/16:0:(ih-iw*9/16)/2,scale=1920:1080:flags=lanczos,subtitles='${esc(`${outDir}/subs_16x9.ass`)}'`,
  ...video, ...audio, `${outDir}/npc_talk_16x9.mp4`]);
console.log(`-> ${outDir}/npc_talk_16x9.mp4`);

// 9:16: whoever's on camera is in the middle of every shot; a 4:5 window of it over a blurred fill.
await run("ffmpeg", [...concat, "-filter_complex",
  "[0:v]split[a][b];" +
  "[a]scale=-2:1920,crop=1080:1920,boxblur=24:2,eq=brightness=-0.12[bg];" +
  "[b]crop=ih*4/5:ih:(iw-ih*4/5)/2:0,scale=1080:1350:flags=lanczos[fg];" +
  `[bg][fg]overlay=0:285,subtitles='${esc(`${outDir}/subs_9x16.ass`)}'[v]`,
  "-map", "[v]", "-map", "0:a", ...video, ...audio, `${outDir}/npc_talk_9x16.mp4`]);
console.log(`-> ${outDir}/npc_talk_9x16.mp4`);

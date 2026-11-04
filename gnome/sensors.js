// Sensor reads for the GNOME edition. Everything LibreHardwareMonitor + PawnIO
// had to dig out of kernel drivers on Windows is plain sysfs on Linux:
//   CPU temp  hwmon "k10temp" (Tctl) / "coretemp" (Package)
//   iGPU      hwmon "amdgpu" (edge) + device/gpu_busy_percent
//   fans      hwmon "dell_smm" / any hwmon with fanN_input (+ fanN_max when the driver knows it)
//   NVIDIA    nvidia-smi, streamed — see NvidiaReader
// Reads are async (Gio worker threads): some fan drivers (dell_smm) answer via
// SMM/ACPI calls that can take milliseconds, which must never stall the shell.
import GLib from "gi://GLib";
import Gio from "gi://Gio";

Gio._promisify(Gio.File.prototype, "load_contents_async");
Gio._promisify(Gio.DataInputStream.prototype, "read_line_async");

const HWMON = "/sys/class/hwmon";
const dec = new TextDecoder();

async function readText(path) {
  try {
    const [bytes] = await Gio.File.new_for_path(path).load_contents_async(null);
    return dec.decode(bytes).trim();
  } catch (e) {
    return null;
  }
}
const readNum = async (path) => { const t = await readText(path); return t === null || t === "" ? null : Number(t); };

function listDir(path) {
  const out = [];
  try {
    const en = Gio.File.new_for_path(path).enumerate_children("standard::name", Gio.FileQueryInfoFlags.NONE, null);
    let info;
    while ((info = en.next_file(null))) out.push(info.get_name());
  } catch (e) {}
  return out.sort();
}

// One-time discovery of which files to poll (sync is fine: runs once on enable).
export function discover() {
  const s = { cpuTemp: null, igpu: null, fans: [], extraTemps: [] };
  for (const h of listDir(HWMON)) {
    const dir = `${HWMON}/${h}`;
    let name = "";
    try { name = dec.decode(GLib.file_get_contents(`${dir}/name`)[1]).trim(); } catch (e) { continue; }
    const files = listDir(dir);
    const label = (f) => { try { return dec.decode(GLib.file_get_contents(`${dir}/${f}`)[1]).trim(); } catch (e) { return null; } };
    if (name === "k10temp" || name === "coretemp") {
      // Tctl (AMD) / Package id 0 (Intel); fall back to temp1.
      const t = files.filter((f) => /^temp\d+_label$/.test(f)).find((f) => /Tctl|Package/.test(label(f) || ""));
      s.cpuTemp = `${dir}/${t ? t.replace("_label", "_input") : "temp1_input"}`;
    } else if (name === "amdgpu" && files.includes("temp1_input")) {
      s.igpu = { temp: `${dir}/temp1_input`, busy: `${dir}/device/gpu_busy_percent` };
    } else if (name === "nvme") {
      if (files.includes("temp1_input")) s.extraTemps.push({ name: "NVMe" + (s.extraTemps.filter((x) => x.name.startsWith("NVMe")).length || ""), path: `${dir}/temp1_input` });
    }
    // Board temps: labelled temps from any other driver (dell_smm: SODIMM,
    // Ambient...), skipping plain CPU/GPU duplicates and NIC/Wi-Fi sensors.
    if (!["k10temp", "coretemp", "amdgpu", "nvme"].includes(name) && !/wifi|r8169|eth|enp|ucsi|BAT|ACAD/i.test(name)) {
      for (const f of files.filter((f) => /^temp\d+_label$/.test(f))) {
        const l = label(f);
        if (l && !/^(CPU|GPU)$/.test(l) && !s.extraTemps.some((x) => x.name === l))
          s.extraTemps.push({ name: l, path: `${dir}/${f.replace("_label", "_input")}` });
      }
    }
    // Fans: first driver that has them wins (dell_smm and alienware_wmi expose
    // the same two fans). dell_smm goes last — its reads are SMM calls.
    const fanInputs = files.filter((f) => /^fan\d+_input$/.test(f));
    if (fanInputs.length && (!s.fans.length || s.fanDriver === "dell_smm") && name !== s.fanDriver) {
      s.fans = []; s.fanDriver = name;
      for (const f of fanInputs) {
        const n = f.match(/^fan(\d+)_/)[1];
        const max = files.includes(`fan${n}_max`) ? Number(label(`fan${n}_max`)) || 0 : 0;
        s.fans.push({ name: label(`fan${n}_label`) || `Fan ${n}`, path: `${dir}/${f}`, max });
      }
    }
  }
  return s;
}

let lastStat = null;
async function cpuLoad() {
  const t = await readText("/proc/stat");
  if (!t) return null;
  const v = t.split("\n")[0].trim().split(/\s+/).slice(1).map(Number);
  const idle = v[3] + (v[4] || 0), total = v.reduce((a, b) => a + b, 0);
  const prev = lastStat; lastStat = { idle, total };
  if (!prev || total === prev.total) return null;
  return 100 * (1 - (idle - prev.idle) / (total - prev.total));
}

async function ram() {
  const t = await readText("/proc/meminfo");
  if (!t) return null;
  const kb = (k) => Number((t.match(new RegExp(`^${k}:\\s+(\\d+)`, "m")) || [])[1] || 0);
  const total = kb("MemTotal"), avail = kb("MemAvailable");
  if (!total) return null;
  return { load: 100 * (1 - avail / total), usedGb: (total - avail) / 1048576 };
}

export async function readAll(map) {
  const [cpuT, load, mem, igpuT, igpuBusy, fans, extras] = await Promise.all([
    map.cpuTemp ? readNum(map.cpuTemp) : null,
    cpuLoad(),
    ram(),
    map.igpu ? readNum(map.igpu.temp) : null,
    map.igpu ? readNum(map.igpu.busy) : null,
    Promise.all(map.fans.map(async (f) => ({ name: f.name, rpm: await readNum(f.path), max: f.max }))),
    Promise.all(map.extraTemps.map(async (x) => ({ name: x.name, temp: await readNum(x.path) }))),
  ]);
  return {
    cpuTemp: cpuT === null ? null : cpuT / 1000,
    cpuLoad: load,
    ram: mem,
    igpu: igpuT === null ? null : { temp: igpuT / 1000, load: igpuBusy },
    fans: fans.filter((f) => f.rpm !== null),
    extraTemps: extras.filter((x) => x.temp !== null && x.temp > 0).map((x) => ({ name: x.name, temp: x.temp / 1000 })),
  };
}

// NVIDIA dGPU. A one-shot nvidia-smi costs ~40 ms of CPU (NVML init) — too much
// every 2 s — so it runs as one long-lived `-lms` stream instead. But an open
// NVML handle keeps an Optimus dGPU awake, so the stream only runs while the
// dGPU is already awake for its own reasons: it drives a connected display.
// Otherwise the GPU row reads "asleep" and costs nothing.
export class NvidiaReader {
  constructor(intervalMs) {
    this._interval = intervalMs;
    this.last = null; // { temp, load, power } | { asleep: true } | null (no NVIDIA)
    this._card = null;
    for (const c of listDir("/sys/class/drm").filter((c) => /^card\d+$/.test(c))) {
      try {
        const drv = Gio.File.new_for_path(`/sys/class/drm/${c}/device/driver`).query_info("standard::symlink-target", Gio.FileQueryInfoFlags.NOFOLLOW_SYMLINKS, null).get_symlink_target();
        if (drv && drv.endsWith("/nvidia")) this._card = c;
      } catch (e) {}
    }
    this.present = !!this._card && !!GLib.find_program_in_path("nvidia-smi");
  }

  _drivesDisplay() {
    return listDir("/sys/class/drm").filter((c) => c.startsWith(this._card + "-")).some((c) => {
      try { return dec.decode(GLib.file_get_contents(`/sys/class/drm/${c}/status`)[1]).trim() === "connected"; } catch (e) { return false; }
    });
  }

  // Called every poll: start/stop the stream as the dGPU's display state changes.
  update() {
    if (!this.present) return;
    const want = this._drivesDisplay();
    // A stream that died on its own is retried at most every 30 s (each start costs NVML init).
    if (want && !this._proc && GLib.get_monotonic_time() - (this._exitedAt || 0) > 30e6) this._start();
    else if (!want && this._proc) this._stop();
    if (!want) this.last = { asleep: true };
  }

  _start() {
    try {
      this._proc = Gio.Subprocess.new(["nvidia-smi", "--query-gpu=temperature.gpu,utilization.gpu,power.draw",
        "--format=csv,noheader,nounits", `-lms`, String(this._interval)], Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
    } catch (e) { this._proc = null; return; }
    const proc = this._proc;
    const stream = new Gio.DataInputStream({ base_stream: proc.get_stdout_pipe() });
    this._cancel = new Gio.Cancellable();
    const cancel = this._cancel;
    (async () => {
      try {
        for (;;) {
          const [line] = await stream.read_line_async(GLib.PRIORITY_LOW, cancel);
          if (line === null) break;
          const [temp, load, power] = dec.decode(line).split(",").map((x) => Number(x.trim()));
          this.last = { temp: isNaN(temp) ? null : temp, load: isNaN(load) ? null : load, power: isNaN(power) ? null : power };
        }
      } catch (e) {}
      if (this._proc === proc) { this._proc = null; this.last = null; this._exitedAt = GLib.get_monotonic_time(); } // exited on its own
    })();
  }

  _stop() {
    const proc = this._proc;
    this._proc = null;
    this._cancel?.cancel();
    proc?.force_exit();
  }

  destroy() { this._stop(); }
}

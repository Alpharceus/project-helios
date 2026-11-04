// Helios for GNOME Shell: CPU/GPU temperature in the top bar, colored as the
// four-level heat gauge (blue / green / yellow / red, with hysteresis), a
// detail dropdown (loads, RAM, fans as rpm and % of the real max, board
// temps), an optional click-through overlay on a chosen monitor, and
// once-per-episode over-temperature notifications. Config lives in
// ~/.config/helios/config.json (created with defaults on first run).
import GLib from "gi://GLib";
import Gio from "gi://Gio";
import St from "gi://St";
import Clutter from "gi://Clutter";

import * as Main from "resource:///org/gnome/shell/ui/main.js";
import * as PanelMenu from "resource:///org/gnome/shell/ui/panelMenu.js";
import * as PopupMenu from "resource:///org/gnome/shell/ui/popupMenu.js";
import * as MessageTray from "resource:///org/gnome/shell/ui/messageTray.js";
import { Extension } from "resource:///org/gnome/shell/extensions/extension.js";

import { discover, readAll, NvidiaReader } from "./sensors.js";

const DEFAULTS = {
  intervalMs: 2000,
  cpuMax: 95, gpuMax: 87,          // alert thresholds (°C) — laptop silicon runs hotter than the desktop's 85/80
  rearm: 5,                        // an alert re-arms this far below its threshold
  cpuBands: [60, 75, 88],          // gauge boundaries: blue < 60 <= green < 75 <= yellow < 88 <= red
  gpuBands: [55, 68, 80],
  hysteresis: 3,                   // the gauge only steps down after falling this far below a boundary
  alerts: true,
  overlay: false,
  overlayMonitor: null,            // connector name, e.g. "HDMI-1"; null = primary
  overlayCorner: "top-right",
  rgb: false,                      // optional OpenRGB hook: whole-device static color per band
  rgbCommand: "openrgb",
};
const BAND_HEX = ["0000FF", "00FF00", "FFFF00", "FF0000"]; // what the RGB hook sends (Windows parity)
const CORNERS = ["top-left", "top-right", "bottom-left", "bottom-right"];

const fmtT = (t) => (t === null || t === undefined ? "--" : Math.round(t) + "°");
const fmtP = (p) => (p === null || p === undefined ? "" : Math.round(p) + "%");

export default class HeliosExtension extends Extension {
  enable() {
    this._cfgPath = GLib.build_filenamev([GLib.get_user_config_dir(), "helios", "config.json"]);
    this._cfg = this._loadConfig();
    this._map = discover();
    this._nv = new NvidiaReader(this._cfg.intervalMs);
    this._band = -1;
    this._armed = { cpu: true, gpu: true };
    this._snap = null;

    this._buildIndicator();
    this._syncOverlay();
    this._monitorsId = Main.layoutManager.connect("monitors-changed", () => this._syncOverlay());
    // Lock screen: the readout moves onto it (see _syncOverlay); unlock moves it back.
    this._sessionId = Main.sessionMode.connect("updated", () => this._syncOverlay());

    this._poll();
    this._timer = GLib.timeout_add(GLib.PRIORITY_LOW, this._cfg.intervalMs, () => { this._poll(); return GLib.SOURCE_CONTINUE; });
  }

  disable() {
    if (this._timer) GLib.source_remove(this._timer);
    this._timer = 0;
    Main.layoutManager.disconnect(this._monitorsId);
    Main.sessionMode.disconnect(this._sessionId);
    this._nv.destroy();
    this._nv = null;
    this._destroyOverlay();
    this._indicator.destroy();
    this._indicator = null;
    this._source?.destroy();
    this._source = null;
    this._rgbProc?.force_exit();
    this._rgbProc = null;
  }

  // ---- config --------------------------------------------------------------
  _loadConfig() {
    let user = {};
    try { user = JSON.parse(new TextDecoder().decode(GLib.file_get_contents(this._cfgPath)[1])); } catch (e) {}
    const cfg = { ...DEFAULTS, ...user };
    if (Object.keys(user).length !== Object.keys(cfg).length) this._saveConfig(cfg); // write out new/default keys
    return cfg;
  }

  _saveConfig(cfg = this._cfg) {
    try {
      GLib.mkdir_with_parents(GLib.path_get_dirname(this._cfgPath), 0o755);
      GLib.file_set_contents(this._cfgPath, JSON.stringify(cfg, null, 2) + "\n");
    } catch (e) { console.error("[helios] config save failed: " + e.message); }
  }

  // ---- polling -------------------------------------------------------------
  async _poll() {
    if (this._busy) return; // a slow fan read must not stack polls
    this._busy = true;
    try {
      this._nv.update();
      const s = await readAll(this._map);
      if (!this._indicator) return; // disabled meanwhile
      s.gpu = this._nv.present ? this._nv.last : null;
      this._snap = s;
      this._updateGauge(s);
      this._checkAlerts(s);
      this._render(s);
    } catch (e) {
      console.error("[helios] poll: " + e.message);
    } finally {
      this._busy = false;
    }
  }

  // Four-level gauge, higher of CPU/GPU wins; heating reacts at once, cooling
  // only after clearing the hysteresis (a temp on a boundary can't flap).
  _updateGauge(s) {
    const c = this._cfg;
    const bandOf = (t, bounds, shift) => (t === null || t === undefined ? 0 : bounds.filter((b) => t >= b - shift).length);
    const gpuT = s.gpu && !s.gpu.asleep ? s.gpu.temp : null;
    const up = Math.max(bandOf(s.cpuTemp, c.cpuBands, 0), bandOf(gpuT, c.gpuBands, 0));
    const down = Math.max(bandOf(s.cpuTemp, c.cpuBands, c.hysteresis), bandOf(gpuT, c.gpuBands, c.hysteresis));
    let band = this._band;
    if (band < 0 || up > band) band = up;
    else if (down < band) band = down;
    if (band === this._band) return;
    this._band = band;
    if (c.rgb) this._applyRgb(BAND_HEX[band]);
  }

  _checkAlerts(s) {
    const c = this._cfg;
    const gpuT = s.gpu && !s.gpu.asleep ? s.gpu.temp : null;
    const msgs = [];
    for (const [key, t, max, label] of [["cpu", s.cpuTemp, c.cpuMax, "CPU"], ["gpu", gpuT, c.gpuMax, "GPU"]]) {
      if (t === null || t === undefined) continue;
      if (t <= max - c.rearm) this._armed[key] = true;
      if (t >= max && this._armed[key]) {
        this._armed[key] = false;
        msgs.push(`${label} temperature ${Math.round(t)}°C exceeds the ${max}°C limit`);
      }
    }
    if (msgs.length && c.alerts) this._notify("Temperature alert", msgs.join("\n"));
  }

  _notify(title, body) {
    try {
      if (!this._source) {
        this._source = new MessageTray.Source({ title: "Helios", iconName: "temperature-symbolic" });
        this._source.connect("destroy", () => { this._source = null; });
        Main.messageTray.add(this._source);
      }
      const n = new MessageTray.Notification({ source: this._source, title, body, urgency: MessageTray.Urgency.CRITICAL });
      this._source.addNotification(n);
    } catch (e) {
      Main.notify(title, body);
    }
  }

  // Latest state supersedes an in-flight apply; nothing stays resident.
  _applyRgb(hex) {
    this._rgbPending = hex;
    if (this._rgbProc) return;
    const cmd = GLib.find_program_in_path(this._cfg.rgbCommand);
    if (!cmd) return;
    const color = this._rgbPending;
    this._rgbPending = null;
    try {
      this._rgbProc = Gio.Subprocess.new([cmd, "--mode", "static", "--color", color], Gio.SubprocessFlags.STDOUT_SILENCE | Gio.SubprocessFlags.STDERR_SILENCE);
      this._rgbProc.wait_async(null, () => { this._rgbProc = null; if (this._rgbPending) this._applyRgb(this._rgbPending); });
    } catch (e) { this._rgbProc = null; }
  }

  // ---- top bar + dropdown ----------------------------------------------------
  _buildIndicator() {
    this._indicator = new PanelMenu.Button(0.5, "Helios", false);
    this._label = new St.Label({ text: "…", y_align: Clutter.ActorAlign.CENTER, style_class: "helios-panel" });
    this._indicator.add_child(this._label);
    const menu = this._indicator.menu;

    this._rows = new PopupMenu.PopupMenuSection();
    menu.addMenuItem(this._rows);
    menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());

    const toggle = (label, key, after) => {
      const item = new PopupMenu.PopupSwitchMenuItem(label, this._cfg[key]);
      item.connect("toggled", (_i, on) => { this._cfg[key] = on; this._saveConfig(); after?.(); });
      menu.addMenuItem(item);
      return item;
    };
    toggle("Floating overlay", "overlay", () => this._syncOverlay());
    toggle("Over-temperature alerts", "alerts");
    if (GLib.find_program_in_path(this._cfg.rgbCommand))
      toggle("RGB heat gauge (OpenRGB)", "rgb", () => { this._band = -1; });

    this._monitorMenu = new PopupMenu.PopupSubMenuMenuItem("Overlay monitor");
    menu.addMenuItem(this._monitorMenu);
    menu.connect("open-state-changed", (_m, open) => { if (open) this._fillMonitorMenu(); });

    const cornerMenu = new PopupMenu.PopupSubMenuMenuItem("Overlay corner");
    for (const corner of CORNERS) {
      const item = new PopupMenu.PopupMenuItem(corner.replace("-", " "));
      item.setOrnament(corner === this._cfg.overlayCorner ? PopupMenu.Ornament.CHECK : PopupMenu.Ornament.NONE);
      item.connect("activate", () => {
        this._cfg.overlayCorner = corner; this._saveConfig(); this._syncOverlay();
        cornerMenu.menu._getMenuItems().forEach((i) => i.setOrnament(i === item ? PopupMenu.Ornament.CHECK : PopupMenu.Ornament.NONE));
      });
      cornerMenu.menu.addMenuItem(item);
    }
    menu.addMenuItem(cornerMenu);

    const cfgItem = new PopupMenu.PopupMenuItem("Edit thresholds & bands…");
    cfgItem.connect("activate", () => Gio.AppInfo.launch_default_for_uri(Gio.File.new_for_path(this._cfgPath).get_uri(), null));
    menu.addMenuItem(cfgItem);

    Main.panel.addToStatusArea(this.uuid, this._indicator);
  }

  _fillMonitorMenu() {
    this._monitorMenu.menu.removeAll();
    const current = this._overlayMonitorIndex();
    Main.layoutManager.monitors.forEach((m, i) => {
      const connector = this._connectorOf(i);
      const item = new PopupMenu.PopupMenuItem(`${connector || "Monitor " + (i + 1)}  ${m.width}×${m.height}${i === Main.layoutManager.primaryIndex ? "  (primary)" : ""}`);
      item.setOrnament(i === current ? PopupMenu.Ornament.CHECK : PopupMenu.Ornament.NONE);
      item.connect("activate", () => { this._cfg.overlayMonitor = connector; this._saveConfig(); this._syncOverlay(); });
      this._monitorMenu.menu.addMenuItem(item);
    });
  }

  _connectorOf(index) {
    const mm = global.backend.get_monitor_manager();
    for (const lm of mm.get_logical_monitors?.() || []) {
      if (lm.get_number() !== index) continue;
      const mon = lm.get_monitors()[0];
      return mon ? mon.get_connector() : null;
    }
    return null;
  }

  _overlayMonitorIndex() {
    if (this._cfg.overlayMonitor) {
      const i = global.backend.get_monitor_manager().get_monitor_for_connector(this._cfg.overlayMonitor);
      if (i >= 0) return i;
    }
    return Main.layoutManager.primaryIndex;
  }

  // Rows shared by the dropdown and the overlay: [name, value, hot?, dim?]
  _rowsFor(s) {
    const c = this._cfg;
    const rows = [];
    rows.push(["CPU", `${fmtT(s.cpuTemp)}C  ${fmtP(s.cpuLoad)}`, s.cpuTemp >= c.cpuMax, false]);
    if (s.gpu) {
      if (s.gpu.asleep) rows.push(["GPU", "asleep", false, true]);
      else rows.push(["GPU", `${fmtT(s.gpu.temp)}C  ${fmtP(s.gpu.load)}${s.gpu.power ? "  " + Math.round(s.gpu.power) + " W" : ""}`, s.gpu.temp >= c.gpuMax, false]);
    }
    if (s.igpu) rows.push(["iGPU", `${fmtT(s.igpu.temp)}C  ${fmtP(s.igpu.load)}`, false, true]);
    if (s.ram) rows.push(["RAM", `${fmtP(s.ram.load)}  ${s.ram.usedGb.toFixed(1)} GB`, false, false]);
    for (const f of s.fans) rows.push([f.name, `${f.rpm} rpm${f.max ? "  " + Math.min(100, Math.round((100 * f.rpm) / f.max)) + "%" : ""}`, false, true]);
    return rows;
  }

  _render(s) {
    const c = this._cfg;
    const gpuT = s.gpu && !s.gpu.asleep ? s.gpu.temp : null;
    const hot = s.cpuTemp >= c.cpuMax || (gpuT !== null && gpuT >= c.gpuMax);
    this._label.text = `${fmtT(s.cpuTemp)}${gpuT !== null ? "  " + fmtT(gpuT) : ""}`;
    this._label.style_class = `helios-panel helios-band-${Math.max(0, this._band)}${hot ? " helios-hot" : ""}`;

    // Dropdown: rebuild rows only while it's open (cheap either way, but no point).
    if (this._indicator.menu.isOpen || !this._rowsBuilt) {
      this._rowsBuilt = true;
      this._rows.removeAll();
      const detail = this._rowsFor(s).concat(s.extraTemps.map((x) => [x.name, fmtT(x.temp) + "C", false, true]));
      for (const [name, value, isHot, dim] of detail) {
        const item = new PopupMenu.PopupBaseMenuItem({ reactive: false, can_focus: false });
        item.add_child(new St.Label({ text: name, x_expand: true, style_class: "helios-row-name" + (dim ? " helios-dim" : "") }));
        item.add_child(new St.Label({ text: value, style_class: "helios-row-value" + (isHot ? " helios-hot" : dim ? " helios-dim" : "") }));
        this._rows.addMenuItem(item);
      }
    }
    this._renderOverlay(s);
  }

  // ---- overlay: click-through chrome, always above windows --------------------
  // While locked it's shown regardless of the toggle, on top of the lock screen
  // (read-only labels; the top-bar menu isn't reachable there).
  _syncOverlay() {
    this._destroyOverlay();
    const locked = Main.sessionMode.isLocked;
    // Display-only on the lock screen: its menu can launch an editor and flip settings.
    if (locked) this._indicator.menu.close();
    this._indicator.reactive = !locked;
    if (!this._cfg.overlay && !locked) return;
    this._overlay = new St.BoxLayout({ orientation: Clutter.Orientation.VERTICAL, style_class: "helios-overlay", reactive: false });
    this._overlayOnLock = locked;
    if (locked) Main.layoutManager.screenShieldGroup.add_child(this._overlay); // last child = above the lock dialog
    else Main.layoutManager.addTopChrome(this._overlay, { affectsStruts: false, trackFullscreen: false }); // reactive: false -> clicks pass through
    this._overlay.connect("notify::allocation", () => this._placeOverlay());
    if (this._snap) this._renderOverlay(this._snap);
  }

  _placeOverlay() {
    if (!this._overlay) return;
    const m = Main.layoutManager.monitors[this._overlayMonitorIndex()];
    if (!m) return;
    const margin = 12;
    const panelH = this._overlayMonitorIndex() === Main.layoutManager.primaryIndex ? Main.panel.height : 0;
    const [w, h] = [this._overlay.width, this._overlay.height];
    const corner = this._cfg.overlayCorner;
    const x = corner.endsWith("left") ? m.x + margin : m.x + m.width - w - margin;
    const y = corner.startsWith("top") ? m.y + margin + panelH : m.y + m.height - h - margin;
    if (this._overlay.x !== x || this._overlay.y !== y) this._overlay.set_position(x, y);
  }

  _renderOverlay(s) {
    if (!this._overlay) return;
    const rows = this._rowsFor(s);
    const kids = this._overlay.get_children();
    // Reuse label pairs; only text/style changes between polls.
    while (kids.length > rows.length) kids.pop().destroy();
    rows.forEach(([name, value, hot, dim], i) => {
      let row = kids[i];
      if (!row) {
        row = new St.BoxLayout({ style_class: "helios-overlay-row" });
        row.add_child(new St.Label({ x_expand: true }));
        row.add_child(new St.Label({ x_align: Clutter.ActorAlign.END }));
        this._overlay.add_child(row);
      }
      const [a, b] = row.get_children();
      a.text = name; b.text = value;
      const big = i < 2 && !dim;
      a.style_class = "helios-overlay-name" + (big ? " helios-big" : "") + (dim ? " helios-dim" : "");
      b.style_class = "helios-overlay-value" + (big ? " helios-big" : "") + (hot ? " helios-hot" : dim ? " helios-dim" : "");
    });
    this._overlay.style_class = `helios-overlay helios-edge-${Math.max(0, this._band)}`;
    this._placeOverlay();
  }

  _destroyOverlay() {
    if (!this._overlay) return;
    if (!this._overlayOnLock) Main.layoutManager.removeChrome(this._overlay);
    this._overlay.destroy();
    this._overlay = null;
  }
}

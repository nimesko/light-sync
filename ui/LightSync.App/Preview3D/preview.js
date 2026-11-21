// LightSync 3D setup preview. Runs inside the app's WebView2, served from https://lightsync.preview/.
// Units are millimetres; Y is up, the case's front faces +Z and its window side faces -X.
// Talks to the app only through chrome.webview messages (see SetupPreview3D.cs for the other side).

import * as THREE from 'three';
import { OrbitControls } from './vendor/three/OrbitControls.js';
import { TransformControls } from './vendor/three/TransformControls.js';

const webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;
const post = (m) => { if (webview) { webview.postMessage(m); } };
const $ = (id) => document.getElementById(id);

const DEG = Math.PI / 180;
const V3 = THREE.Vector3;
const Q = THREE.Quaternion;
const AXES = ['x', 'y', 'z'];
const FACES = ['front', 'top', 'bottom', 'rear', 'side'];
const DESK_W = 1500, DESK_D = 700, DESK_T = 25, LEG_H = 720;

const canvas = $('view');
const tip = $('tip');

// ---------- renderer, camera, controls ----------

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(38, 1, 5, 60000);
camera.position.set(-1200, 1100, 1500);
scene.add(new THREE.HemisphereLight(0xffffff, 0x3a3f4a, 1.5));
const sun = new THREE.DirectionalLight(0xffffff, 2.0);
sun.position.set(-900, 1800, 1400);
scene.add(sun);
const fillLight = new THREE.DirectionalLight(0xffffff, 0.6);
fillLight.position.set(1200, 600, -800);
scene.add(fillLight);

let dirty = true;
function requestRender() { dirty = true; }

const orbit = new OrbitControls(camera, canvas);
orbit.enableDamping = false;
orbit.screenSpacePanning = true;
orbit.zoomToCursor = true;
orbit.minDistance = 60;
orbit.maxDistance = 25000;
orbit.rotateSpeed = 0.8;
orbit.zoomSpeed = 1.2;
// One button, one job, in normal and Arrange mode alike: left turns the view, right moves it, middle
// (or the wheel) zooms. Shift / Ctrl + left also moves (laptops). Double-click centres on a part.
orbit.mouseButtons = { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
orbit.addEventListener('change', requestRender);
orbit.addEventListener('end', saveCameraSoon);

// Pressing on the gizmo moves the part, not the view: turn the view controls off before they see the press.
canvas.addEventListener('pointerdown', (e) => {
  if (arrange && e.button === 0 && tc.object && tc.axis) { orbit.enabled = false; }
}, true);
window.addEventListener('pointerup', () => { if (!tc.dragging) { orbit.enabled = true; } });

const tc = new TransformControls(camera, canvas);
tc.setSize(0.85);
tc.setSpace('world');
tc.addEventListener('change', requestRender);
tc.addEventListener('dragging-changed', (e) => {
  orbit.enabled = !e.value;
  if (e.value) { dragStart(); } else { dragEnd(); }
});
tc.addEventListener('objectChange', onDrag);
scene.add(tc);

// Snapping guides: up to three lines (one per axis) and the outline of a case fan slot.
const guideGeo = new THREE.BufferGeometry();
guideGeo.setAttribute('position', new THREE.Float32BufferAttribute(new Float32Array(18), 3));
guideGeo.setAttribute('color', new THREE.Float32BufferAttribute(new Float32Array(18), 3));
const guides = new THREE.LineSegments(guideGeo, new THREE.LineBasicMaterial({ vertexColors: true, depthTest: false, transparent: true }));
guides.renderOrder = 10;
guides.frustumCulled = false;
guides.visible = false;
scene.add(guides);
const slotGuide = new THREE.LineLoop(
  new THREE.BufferGeometry().setFromPoints([new V3(-62, -62, 0), new V3(62, -62, 0), new V3(62, 62, 0), new V3(-62, 62, 0)]),
  new THREE.LineBasicMaterial({ color: 0x33d17a, depthTest: false, transparent: true }));
slotGuide.renderOrder = 10;
slotGuide.visible = false;
scene.add(slotGuide);
const AXIS_COLORS = { x: new THREE.Color(0xff5050), y: new THREE.Color(0x50e070), z: new THREE.Color(0x5090ff) };

// ---------- state ----------

let theme = { dark: true, accent: '#4cc2ff' };
let pal = palette();
let parts = [];            // from the app: { id, name, type, typeLabel, desk, source, start, span, leds }
const partInfo = new Map(); // id -> part
let overrides = {};        // id -> [x, y, z, rx, ry, rz] the user set (local to the case / desk, degrees)
// A dual-chamber glass case: the board at the back, a column of three fans in front of it, three on top,
// one at the rear, two on the PSU shroud under the graphics card and the CPU cooler's lit cap.
let caseSize = [290, 470, 470];
const SHROUD_H = 90;
/** LEDs of decorations (unmapped fans, the shroud's light bar) show the case's main colour. */
const AMBIENT = '@ambient';
let spareFans = [];         // default fan places no part took: drawn as fans in the case's colour
let glow = null;
let arrange = false;
let hostSel = new Set(['all']);
let editSel = [];          // arrange selection; the last one carries the gizmo
let lastSources = {};
let world = null, rootCase = null, rootDesk = null, shell = null, deskTop = null;
const objects = new Map();  // id -> Object3D (parts, '@case', '@desk')
const defaults = new Map(); // id -> default transform
let pickRoots = [];
let leds = [];
let helpers = [];
const undoStack = [], redoStack = [];
let drag = null;
let hasCamera = false;

const prefs = loadPrefs();
scene.background = new THREE.Color(pal.bg);

function palette() {
  return theme.dark
    ? { bg: 0x1f2126, shell: 0x9aa3b5, edge: 0x8a93a5, body: 0x2b2e35, detail: 0x4a505c, board: 0x26302c, desk: 0x4a4038, leg: 0x2c2e33, unlit: 0x161616 }
    : { bg: 0xeceef2, shell: 0x6a7385, edge: 0x6f7787, body: 0x3a3e46, detail: 0x5c6370, board: 0x2f3d36, desk: 0xc8b8a4, leg: 0x8a8d94, unlit: 0x1c1c1c };
}

function loadPrefs() {
  const p = { grid: false, gridSize: 10, snapParts: true };
  try {
    const s = JSON.parse(window.localStorage.getItem('lightsync.preview3d') || '{}');
    if (typeof s.grid === 'boolean') { p.grid = s.grid; }
    if (Number.isFinite(s.gridSize) && s.gridSize >= 1 && s.gridSize <= 500) { p.gridSize = s.gridSize; }
    if (typeof s.snapParts === 'boolean') { p.snapParts = s.snapParts; }
  } catch (_) { /* storage can be unavailable */ }
  return p;
}

function savePrefs() {
  try { window.localStorage.setItem('lightsync.preview3d', JSON.stringify(prefs)); } catch (_) { /* ignore */ }
}

// ---------- small helpers ----------

const r1 = (v) => Math.round(v * 10) / 10 || 0;
const finite = (a, n) => Array.isArray(a) && a.length === n && a.every((v) => typeof v === 'number' && Number.isFinite(v));

function readT(o) {
  return [r1(o.position.x), r1(o.position.y), r1(o.position.z), r1(o.rotation.x / DEG), r1(o.rotation.y / DEG), r1(o.rotation.z / DEG)];
}

function applyT(o, t) {
  o.position.set(t[0], t[1], t[2]);
  o.rotation.set(t[3] * DEG, t[4] * DEG, t[5] * DEG, 'XYZ');
  o.updateMatrixWorld(true);
}

const sameT = (a, b) => a && b && a.every((v, i) => Math.abs(v - b[i]) < 0.05);

function setWorldPos(o, wp) {
  o.parent.updateMatrixWorld(true);
  o.position.copy(o.parent.worldToLocal(wp.clone()));
  o.updateMatrixWorld(true);
}

function moveWorld(o, d) { setWorldPos(o, o.getWorldPosition(new V3()).add(d)); }

function setWorldQuat(o, wq) {
  const pq = o.parent.getWorldQuaternion(new Q()).invert();
  o.quaternion.copy(pq.multiply(wq));
  o.updateMatrixWorld(true);
}

const boxOf = (o) => new THREE.Box3().setFromObject(o);

function disposeTree(o) {
  o.traverse((c) => {
    if (c.geometry) { c.geometry.dispose(); }
    if (c.material) { (Array.isArray(c.material) ? c.material : [c.material]).forEach((m) => { if (m.map) { m.map.dispose(); } m.dispose(); }); }
  });
}

function box(w, h, d, mat) { return new THREE.Mesh(new THREE.BoxGeometry(w, h, d), mat); }
function at(o, x, y, z) { o.position.set(x, y, z); return o; }

/** One LED: shows the colour at its place along the part's share of its device / zone. */
function led(p, k, n, geo) {
  const mat = new THREE.MeshBasicMaterial({ color: pal.unlit });
  const mesh = new THREE.Mesh(geo, mat);
  leds.push({ mat, glows: [], src: p.source, pos: p.start + p.span * (k + 0.5) / n, hex: null });
  mesh.userData.led = leds[leds.length - 1];
  return mesh;
}

/** A soft halo in an LED's colour (light spilling onto the desk or round a key); it is not picked. */
let softTex = null;
/** White in the middle fading to nothing at the edges: light that spills softly. */
function softTexture() {
  if (softTex) { return softTex; }
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d');
  const r = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  r.addColorStop(0, 'rgba(255,255,255,1)');
  r.addColorStop(0.45, 'rgba(255,255,255,0.45)');
  r.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = r;
  g.fillRect(0, 0, 64, 64);
  softTex = new THREE.CanvasTexture(c);
  return softTex;
}

function halo(ledMesh, geo, opacity, soft) {
  const m = new THREE.MeshBasicMaterial({ color: 0x000000, transparent: true, opacity: opacity || 0.35, depthWrite: false, blending: THREE.AdditiveBlending, map: soft ? softTexture() : null });
  ledMesh.userData.led.glows.push(m);
  const g = new THREE.Mesh(geo, m);
  g.userData.noPick = true;
  g.raycast = () => {};
  return g;
}

/** A flat rounded rectangle (X-Y plane) extruded along Z by <depth>, centred. */
function roundedBox(w, h, depth, r, mat, bevel) {
  const s = new THREE.Shape();
  const x = -w / 2, y = -h / 2;
  r = Math.min(r, w / 2, h / 2);
  s.moveTo(x + r, y);
  s.lineTo(x + w - r, y);
  s.quadraticCurveTo(x + w, y, x + w, y + r);
  s.lineTo(x + w, y + h - r);
  s.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
  s.lineTo(x + r, y + h);
  s.quadraticCurveTo(x, y + h, x, y + h - r);
  s.lineTo(x, y + r);
  s.quadraticCurveTo(x, y, x + r, y);
  const b = bevel || 0;
  const geo = new THREE.ExtrudeGeometry(s, { depth: depth - 2 * b, bevelEnabled: b > 0, bevelSize: b, bevelThickness: b, bevelSegments: 3, curveSegments: 10 });
  geo.translate(0, 0, -(depth - 2 * b) / 2);
  return new THREE.Mesh(geo, mat);
}

const segs = (p, max) => Math.max(1, Math.min(max, p.leds | 0));

// ---------- part models (each in its own frame) ----------

let mats = null;

function makeMats() {
  const std = (color, extra) => new THREE.MeshStandardMaterial(Object.assign({ color, roughness: 0.75, metalness: 0.1 }, extra || {}));
  return {
    body: std(pal.body), detail: std(pal.detail), board: std(pal.board), desk: std(pal.desk, { roughness: 0.9 }), leg: std(pal.leg),
    // Peripherals: black anodised aluminium, matte black plastic, dark key caps and speaker fabric.
    alu: std(0x24262b, { roughness: 0.45, metalness: 0.6 }),
    plastic: std(0x16171a, { roughness: 0.6, metalness: 0.05 }),
    key: std(0x1c1d21, { roughness: 0.55, metalness: 0.05 }),
    fabric: std(0x1a1b1e, { roughness: 1.0, metalness: 0 }),
  };
}

/** 120 mm fan facing +Z: square frame, ring, hub and blades, 12 LEDs round the ring. */
function fanModel(p) {
  const g = new THREE.Group();
  for (const [x, y, w, h] of [[0, 56, 120, 8], [0, -56, 120, 8], [56, 0, 8, 104], [-56, 0, 8, 104]]) {
    g.add(at(box(w, h, 25, mats.body), x, y, 0));
  }
  g.add(at(new THREE.Mesh(new THREE.TorusGeometry(52, 3, 8, 48), mats.detail), 0, 0, 10));
  const hub = new THREE.Mesh(new THREE.CylinderGeometry(19, 19, 20, 24), mats.detail);
  hub.rotation.x = Math.PI / 2;
  g.add(hub);
  for (let k = 0; k < 7; k++) {
    const blade = box(30, 3, 14, mats.detail);
    const a = 2 * Math.PI * k / 7;
    blade.position.set(Math.cos(a) * 33, Math.sin(a) * 33, 0);
    blade.rotation.set(0.5 * Math.cos(a + Math.PI / 2), 0.5 * Math.sin(a + Math.PI / 2), a);
    g.add(blade);
  }
  // An invisible disc so clicking through the blades still picks the fan (the raycaster ignores visibility).
  const pick = new THREE.Mesh(new THREE.CylinderGeometry(58, 58, 24, 24), mats.detail);
  pick.rotation.x = Math.PI / 2;
  pick.visible = false;
  g.add(pick);
  const geo = new THREE.SphereGeometry(4.5, 10, 8);
  for (let k = 0; k < 12; k++) {
    const a = Math.PI / 2 - 2 * Math.PI * k / 12;
    g.add(at(led(p, k, 12, geo), Math.cos(a) * 52, Math.sin(a) * 52, 13));
  }
  return g;
}

/** LED strip along Z, 250 mm. */
function stripModel(p) {
  const g = new THREE.Group();
  const len = 250, n = segs(p, 24), seg = len / n;
  g.add(at(box(4, 10, len, mats.body), 4, 0, 0));
  const geo = new THREE.BoxGeometry(6, 8, Math.max(1, seg - 1.5));
  for (let k = 0; k < n; k++) {
    g.add(at(led(p, k, n, geo), -1, 0, -len / 2 + seg * (k + 0.5)));
  }
  return g;
}

/** Memory stick: 40 mm tall off the board (towards -X), 133 mm long (Y), LEDs along its top edge. */
function ramModel(p) {
  const g = new THREE.Group();
  g.add(box(40, 133, 7, mats.detail));
  const n = segs(p, 12), len = 125, seg = len / n;
  const geo = new THREE.BoxGeometry(6, Math.max(1, seg - 1), 8);
  for (let k = 0; k < n; k++) {
    g.add(at(led(p, k, n, geo), -22, len / 2 - seg * (k + 0.5), 0));
  }
  return g;
}

/** ATX motherboard (305 x 244) facing -X, with a socket, slots and an LED row. */
function boardModel(p) {
  const g = new THREE.Group();
  g.add(box(2, 305, 244, mats.board));
  g.add(at(box(5, 60, 60, mats.detail), -3, 70, 10));
  g.add(at(box(14, 60, 22, mats.detail), -8, 110, -105));
  for (const y of [-30, -75, -120]) {
    g.add(at(box(5, 8, 170, mats.detail), -3, y, -20));
  }
  const n = segs(p, 24), len = 220, seg = len / n;
  const geo = new THREE.BoxGeometry(3, 5, Math.max(1, seg - 1.5));
  for (let k = 0; k < n; k++) {
    g.add(at(led(p, k, n, geo), -3, -146, -len / 2 + seg * (k + 0.5)));
  }
  return g;
}

/** Any other lit part in the case: a small bar along Z. */
function caseOtherModel(p) {
  const g = new THREE.Group();
  g.add(box(16, 20, 120, mats.body));
  const n = segs(p, 12), seg = 110 / n;
  const geo = new THREE.BoxGeometry(3, 10, Math.max(1, seg - 1.5));
  for (let k = 0; k < n; k++) {
    g.add(at(led(p, k, n, geo), -9, 0, -55 + seg * (k + 0.5)));
  }
  return g;
}

/** Desk parts stand on y = 0, their front (the side facing you) towards +Z. */

/**
 * Full-size low-profile keyboard (437 x 140 mm, about 20 mm tall at the back): brushed black aluminium
 * top plate, flat low-profile caps with per-key light (lit legend and the glow round each cap), and the
 * media roller with its button top right, above the number pad. Keys are lit in row order, left to right.
 */
function keyboardModel(p) {
  const g = new THREE.Group();
  const W = 437, D = 140, U = 18.6;
  const plate = roundedBox(W, D, 13, 7, mats.alu, 1.2);
  plate.rotation.x = -Math.PI / 2;
  g.add(at(plate, 0, 6.5, 0));
  // Rows: widths in key units; [w] is a gap, {w, h} a key two rows tall (number pad + and Enter).
  const T = (w) => ({ w, h: 2 });
  const rows = [
    { y: 0, main: [1, [1], 1, 1, 1, 1, [0.5], 1, 1, 1, 1, [0.5], 1, 1, 1, 1], nav: [1, 1, 1], pad: [] },
    { y: 1.4, main: [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2], nav: [1, 1, 1], pad: [1, 1, 1, 1] },
    { y: 2.4, main: [1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5], nav: [1, 1, 1], pad: [1, 1, 1, T(1)] },
    { y: 3.4, main: [1.75, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.25], nav: [], pad: [1, 1, 1] },
    { y: 4.4, main: [2.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.75], nav: [[1], 1], pad: [1, 1, 1, T(1)] },
    { y: 5.4, main: [1.25, 1.25, 1.25, 6.25, 1.25, 1.25, 1.25, 1.25], nav: [1, 1, 1], pad: [2, 1] },
  ];
  const keys = [];
  const total = 23, x0 = -total * U / 2, z0 = -D / 2 + 9;
  for (const r of rows) {
    for (const [start, spec] of [[0, r.main], [15.5, r.nav], [19, r.pad]]) {
      let x = start;
      for (const s of spec) {
        if (Array.isArray(s)) { x += s[0]; continue; }
        const w = typeof s === 'number' ? s : s.w, h = typeof s === 'number' ? 1 : s.h;
        keys.push({ x: x0 + (x + w / 2) * U, z: z0 + (r.y + h / 2) * U, w: w * U - 2.4, d: h * U - 2.4 });
        x += w;
      }
    }
  }
  const capH = 2.6, top = 13;
  const legend = new THREE.BoxGeometry(5, 0.4, 3.2);
  keys.forEach((k, i) => {
    g.add(at(box(k.w, capH, k.d, mats.key), k.x, top + capH / 2, k.z));
    const l = led(p, i, keys.length, legend);
    // The legend sits towards the top of the cap, like printed characters.
    g.add(at(l, k.x, top + capH + 0.2, k.z - k.d * 0.18));
    // Light under the cap shows round its edges.
    const rim = halo(l, new THREE.BoxGeometry(k.w + 1.6, 0.3, k.d + 1.6), 0.55);
    g.add(at(rim, k.x, top + 0.2, k.z));
  });
  // Media roller and button above the number pad.
  const roller = new THREE.Mesh(new THREE.CylinderGeometry(7.5, 7.5, 20, 28), mats.alu);
  roller.rotation.z = Math.PI / 2;
  g.add(at(roller, x0 + 21.8 * U, top + 3, z0 + 0.5 * U));
  const btn = new THREE.Mesh(new THREE.CylinderGeometry(5, 5, 2.5, 20), mats.key);
  g.add(at(btn, x0 + 20.2 * U, top + 1.3, z0 + 0.5 * U));
  return g;
}

/**
 * Small symmetrical wireless mouse (120 x 62 x 38 mm): a smooth hump towards the palm, lit scroll wheel,
 * lit emblem on the palm and an underglow strip round its base. LEDs: wheel, emblem, then the underglow.
 */
function mouseModel(p) {
  const g = new THREE.Group();
  const L = 120, Wd = 62, H = 37;
  const base = roundedBox(Wd - 2, L - 4, 4, 26, mats.plastic, 0.8);
  base.rotation.x = -Math.PI / 2;
  g.add(at(base, 0, 2.5, 0));
  const shell = new THREE.Mesh(new THREE.SphereGeometry(1, 40, 20, 0, Math.PI * 2, 0, Math.PI / 2), mats.plastic);
  shell.scale.set(Wd / 2, H - 4, L / 2);
  g.add(at(shell, 0, 4, 2));
  const sideY = (z) => 4 + (H - 4) * Math.sqrt(Math.max(0, 1 - ((z - 2) / (L / 2)) ** 2));
  // The split between the two buttons, in front of the wheel.
  const split = box(0.8, 1, 16, mats.key);
  split.rotation.x = 0.5;
  g.add(at(split, 0, sideY(-44) - 0.2, -44));
  // Scroll wheel (lit).
  const wheelGeo = new THREE.CylinderGeometry(10, 10, 7, 24);
  wheelGeo.rotateZ(Math.PI / 2);
  const under = Math.max(4, Math.min(14, (p.leds | 0) > 2 ? (p.leds | 0) - 2 : 10));
  const n = under + 2;
  const wheelLed = led(p, 0, n, wheelGeo);
  g.add(at(wheelLed, 0, sideY(-30) - 6.5, -30));
  // Palm emblem: a small lit disc lying on the shell.
  const emblem = led(p, 1, n, new THREE.CylinderGeometry(5, 5, 0.6, 24));
  emblem.rotation.x = 0.5;
  g.add(at(emblem, 0, sideY(30) + 1.2, 30));
  // Underglow round the base, with the light it throws on the desk.
  const seg = new THREE.BoxGeometry(5, 1.6, 5);
  for (let k = 0; k < under; k++) {
    const a = 2 * Math.PI * (k + 0.5) / under;
    const x = Math.sin(a) * (Wd / 2 + 0.5), z = -Math.cos(a) * (L / 2 - 2);
    const l = led(p, k + 2, n, seg);
    g.add(at(l, x, 1.2, z));
    const spill = halo(l, new THREE.PlaneGeometry(30, 30), 0.5, true);
    spill.rotation.x = -Math.PI / 2;
    g.add(at(spill, x * 1.25, 0.3, z * 1.12));
  }
  return g;
}

/**
 * Compact PC soundbar (400 x 72 x 80 mm): a pill-shaped body wrapped in black fabric, control buttons on
 * top at the right, and a light strip along the bottom front edge that washes the desk in front of it.
 */
function soundbarModel(p) {
  const g = new THREE.Group();
  const W = 400, H = 72, D = 80, lift = 5;
  g.add(at(roundedBox(W, H, D, 32, mats.plastic, 2), 0, lift + H / 2, 0));
  // Fabric front, a hair proud of the body.
  g.add(at(roundedBox(W - 8, H - 8, 2, 28, mats.fabric), 0, lift + H / 2, D / 2 + 0.6));
  for (let k = 0; k < 4; k++) {
    const b = new THREE.Mesh(new THREE.CylinderGeometry(4, 4, 1.4, 16), mats.key);
    g.add(at(b, W / 2 - 70 + 14 * k, lift + H + 0.5, -4));
  }
  // Feet.
  for (const x of [-W / 2 + 40, W / 2 - 40]) { g.add(at(box(30, lift, 40, mats.plastic), x, lift / 2, 0)); }
  const n = segs(p, 18), len = W - 70, step = len / n;
  const geo = new THREE.BoxGeometry(Math.max(1, step - 2), 2.5, 10);
  for (let k = 0; k < n; k++) {
    const x = -len / 2 + step * (k + 0.5);
    const l = led(p, k, n, geo);
    g.add(at(l, x, lift + 1, D / 2 - 10));
    const spill = halo(l, new THREE.PlaneGeometry(step * 2.2, 90), 0.35, true);
    spill.rotation.x = -Math.PI / 2;
    g.add(at(spill, x, 0.3, D / 2 + 22));
  }
  return g;
}

/** Any other speaker: a desktop speaker box with a light bar along its front. */
function speakerModel(p) {
  if (p.model === 'soundbar') { return soundbarModel(p); }
  const g = new THREE.Group();
  g.add(at(box(90, 170, 100, mats.plastic), 0, 85, 0));
  const cone = new THREE.Mesh(new THREE.CylinderGeometry(30, 30, 4, 28), mats.detail);
  cone.rotation.x = Math.PI / 2;
  g.add(at(cone, 0, 110, 51));
  const n = segs(p, 10), seg = 76 / n;
  const geo = new THREE.BoxGeometry(Math.max(1, seg - 1.5), 6, 3);
  for (let k = 0; k < n; k++) {
    g.add(at(led(p, k, n, geo), -38 + seg * (k + 0.5), 22, 51));
  }
  return g;
}

/** The case maker's lit logo plate (generic: an outline and an emblem, no brand), facing +Z. */
function logoModel(p) {
  const g = new THREE.Group();
  g.add(roundedBox(150, 44, 4, 10, mats.plastic));
  const n = segs(p, 20);
  const curve = new Stadium(110, 16);
  const geo = new THREE.SphereGeometry(2.4, 8, 6);
  for (let k = 0; k < n; k++) {
    const q = curve.getPoint(k / n);
    g.add(at(led(p, k, n, geo), q.z, q.y, 2.6));
  }
  // A rhombus emblem in the middle, in the colour of the middle LED.
  const shape = new THREE.Shape([new THREE.Vector2(0, 10), new THREE.Vector2(16, 0), new THREE.Vector2(0, -10), new THREE.Vector2(-16, 0)]);
  const emblem = led(p, n >> 1, n, new THREE.ShapeGeometry(shape));
  g.add(at(emblem, 0, 0, 2.3));
  return g;
}

/** The CPU cooler's lit pump cap, facing +Z: a round cap with a light ring and a mirror centre. */
function cpuModel(p) {
  const g = new THREE.Group();
  const cap = new THREE.Mesh(new THREE.CylinderGeometry(38, 40, 22, 40), mats.plastic);
  cap.rotation.x = Math.PI / 2;
  g.add(cap);
  const mirror = new THREE.Mesh(new THREE.CircleGeometry(28, 40), new THREE.MeshStandardMaterial({ color: 0x0b0c0e, roughness: 0.15, metalness: 0.8 }));
  g.add(at(mirror, 0, 0, 11.2));
  const n = segs(p, 16);
  const geo = new THREE.BoxGeometry(6, 6, 2);
  for (let k = 0; k < n; k++) {
    const a = Math.PI / 2 - 2 * Math.PI * k / n;
    const l = led(p, k, n, geo);
    l.rotation.z = a;
    g.add(at(l, Math.cos(a) * 33, Math.sin(a) * 33, 11.5));
    // The mirror repeats the ring further in.
    const echo = halo(l, new THREE.CircleGeometry(2.2, 8), 0.5);
    g.add(at(echo, Math.cos(a) * 20, Math.sin(a) * 20, 11.4));
  }
  return g;
}

function deskOtherModel(p) {
  const g = new THREE.Group();
  g.add(at(box(150, 16, 60, mats.body), 0, 8, 0));
  const n = segs(p, 12), seg = 136 / n;
  const geo = new THREE.BoxGeometry(Math.max(1, seg - 1.5), 4, 10);
  for (let k = 0; k < n; k++) {
    g.add(at(led(p, k, n, geo), -68 + seg * (k + 0.5), 18, 18));
  }
  return g;
}

function makePart(p) {
  switch (p.type) {
    case 'fan': return fanModel(p);
    case 'strip': return stripModel(p);
    case 'ram': return ramModel(p);
    case 'board': return boardModel(p);
    case 'keyboard': return keyboardModel(p);
    case 'mouse': return mouseModel(p);
    case 'speaker': return speakerModel(p);
    default:
      if (!p.desk && p.role === 'logo') { return logoModel(p); }
      if (!p.desk && p.role === 'cpu') { return cpuModel(p); }
      return p.desk ? deskOtherModel(p) : caseOtherModel(p);
  }
}

// ---------- case faces and default places ----------

/** Fan slots on one face of the case (case-local): the plane a fan sits on, its rotation and the slot centres. */
function faceSlots(face) {
  const [W, H, D] = caseSize;
  const m = 25, t = 15;
  const centers = (lo, hi) => {
    const out = [];
    for (let c = hi - 65; c - 65 >= lo - 0.5; c -= 130) { out.push(c); }
    if (!out.length) { out.push((lo + hi) / 2); }
    return out;
  };
  switch (face) {
    case 'front': return { axis: 'z', value: D / 2 - t, rot: [0, 0, 0], slots: centers(m, H - m).map((y) => [0, y, D / 2 - t]) };
    case 'rear': return { axis: 'z', value: -D / 2 + t, rot: [0, 180, 0], slots: centers(m, H - m).map((y) => [0, y, -D / 2 + t]) };
    case 'top': return { axis: 'y', value: H - t, rot: [-90, 0, 0], slots: centers(-D / 2 + m, D / 2 - m).map((z) => [0, H - t, z]) };
    case 'bottom': return { axis: 'y', value: t, rot: [90, 0, 0], slots: centers(-D / 2 + m, D / 2 - m).map((z) => [0, t, z]) };
    default: {
      const slots = [];
      for (const y of centers(m, H - m)) {
        for (const z of centers(-D / 2 + m, D / 2 - m)) { slots.push([-W / 2 + t, y, z]); }
      }
      return { axis: 'x', value: -W / 2 + t, rot: [0, -90, 0], slots };
    }
  }
}

function boardPlace() {
  const [W, H, D] = caseSize;
  return [W / 2 - 25, H - 30 - 152.5, -D / 2 + 30 + 122];
}

/** The case's fan places, by group (case-local [x, y, z, rx, ry, rz]). */
function fanPlan() {
  const [W, H, D] = caseSize;
  const [bx, by, bz] = boardPlace();
  const top = faceSlots('top'), rear = faceSlots('rear');
  return {
    top: top.slots.slice(0, 3).map((s) => [...s, ...top.rot]),
    // Facing the window, stacked in front of the board.
    column: [H - 90, H - 220, H - 350].map((y) => [W / 2 - 40, y, D / 2 - 80, 0, -90, 0]),
    rear: rear.slots.slice(0, 1).map((s) => [...s, ...rear.rot]),
    // Lying on the PSU shroud under the graphics card, blowing up.
    bottom: [-D / 2 + 95, -D / 2 + 225].map((z) => [0, SHROUD_H + 13, z, -90, 0, 0]),
    cpu: [[bx - 45, by + 70, bz + 10, 0, -90, 0]],
  };
}

function defaultPlaces() {
  const [W, H, D] = caseSize;
  const used = {};
  const next = (key) => { const k = used[key] || 0; used[key] = k + 1; return k; };
  // Fans with a known place (the parts of a chained header, e.g. ARGB_V2_2: rear, bottom, side column)
  // take it first; then fans on a fan hub fill the top (the Corsair QX trio), motherboard headers the
  // rear, bottom and side column. The CPU cooler's place is kept for the cooler's own LEDs.
  const plan = fanPlan();
  const order = { hub: ['top', 'column', 'rear', 'bottom'], header: ['rear', 'bottom', 'column', 'top'] };
  const rolePlaces = new Map();
  for (const p of parts) {
    if (p.desk || !p.role) { continue; }
    if (p.type === 'fan' && plan[p.role] && plan[p.role].length) { rolePlaces.set(p.id, plan[p.role].shift()); }
    else if (p.role === 'cpu' && plan.cpu.length) { rolePlaces.set(p.id, plan.cpu.shift()); }
    else if (p.role === 'logo') { rolePlaces.set(p.id, logoPlace()); }
  }
  const extra = [];
  for (const [face, count] of [['front', 3], ['side', 6]]) {
    const f = faceSlots(face);
    f.slots.slice(0, count).forEach((s) => extra.push([...s, ...f.rot]));
  }
  const fanPlaces = new Map();
  const fans = parts.filter((p) => !p.desk && p.type === 'fan' && !rolePlaces.has(p.id));
  let k = 0;
  for (const p of [...fans.filter((f) => f.hub), ...fans.filter((f) => !f.hub)]) {
    const group = (p.hub ? order.hub : order.header).find((g) => plan[g].length);
    fanPlaces.set(p.id, group ? plan[group].shift()
      : extra.length ? extra.shift() : [-30 + 15 * (k % 5), H / 2 - 20 * Math.floor(k / 5), 0, 0, 0, 0]);
    if (!group && !extra.length) { k++; }
  }
  spareFans = Object.values(plan).flat();
  const [boardX, boardY, boardZ] = boardPlace();
  const out = new Map();
  for (const p of parts) {
    let t;
    if (rolePlaces.has(p.id)) {
      t = rolePlaces.get(p.id);
    } else if (p.desk && p.model === 'soundbar') {
      // Centred behind the keyboard, where it sits under a monitor.
      t = [100, 0, -200 + 95 * next('desk:soundbar'), 0, 0, 0];
    } else if (p.desk) {
      const k = next('desk:' + p.type);
      switch (p.type) {
        case 'keyboard': t = [100 + 480 * k, 0, 150, 0, 0, 0]; break;
        case 'mouse': t = [400 + 90 * k, 0, 160, 0, 0, 0]; break;
        case 'speaker': t = [(k % 2 ? 340 : -140) + 110 * Math.floor(k / 2) * (k % 2 ? 1 : -1), 0, -170, 0, 0, 0]; break;
        default: t = [-20 + 170 * (k % 4), 0, 10 - 90 * Math.floor(k / 4), 0, 0, 0]; break;
      }
    } else {
      const k = next('case:' + p.type);
      switch (p.type) {
        case 'fan': t = fanPlaces.get(p.id); break;
        case 'board': t = [boardX - 4 * k, boardY - 10 * k, boardZ + 10 * k, 0, 0, 0]; break;
        case 'ram': t = [boardX - 21, boardY + 50, boardZ + 75 + 12 * k, 0, 0, 0]; break;
        // Along the board's top edge, above the CPU cooler (out of the way of the top fans).
        case 'strip': t = [boardX - 6 - 12 * Math.floor(k / 8), boardY + 140 - 14 * (k % 8), boardZ, 0, 0, 0]; break;
        default: t = [0, 80 + 28 * (k % 12), -D / 2 + 100 + 130 * Math.floor(k / 12), 0, 0, 0]; break;
      }
    }
    out.set(p.id, t);
  }
  return out;
}

// ---------- the case's fixed contents (not devices LightSync controls) ----------

/** A closed rounded bar (stadium) in the Y-Z plane: straight runs along Z, half circles at the ends. */
class Stadium extends THREE.Curve {
  constructor(len, r) { super(); this.len = len; this.r = r; }
  getPoint(t, out = new V3()) {
    const L = this.len, r = this.r;
    let s = t * (2 * L + 2 * Math.PI * r);
    if (s < L) { return out.set(0, r, -L / 2 + s); }
    s -= L;
    if (s < Math.PI * r) { const a = s / r; return out.set(0, r * Math.cos(a), L / 2 + r * Math.sin(a)); }
    s -= Math.PI * r;
    if (s < L) { return out.set(0, -r, L / 2 - s); }
    s -= L;
    const a = s / r;
    return out.set(0, -r * Math.cos(a), -L / 2 - r * Math.sin(a));
  }
}

/** The PSU shroud's length and centre along Z (case-local). */
function shroudSpan() {
  const D = caseSize[2];
  const shroudD = D - 165;
  return { shroudD, shroudZ: -D / 2 + 5 + shroudD / 2 };
}

/** The lit logo plate sits on the shroud's window side, facing the window (case-local transform). */
function logoPlace() {
  const W = caseSize[0];
  const { shroudZ } = shroudSpan();
  return [-(W - 10) / 2 - 3, SHROUD_H / 2, shroudZ, 0, -90, 0];
}

/**
 * PSU shroud (with a light bar when no logo part is mapped) and the fans no device is mapped to yet.
 * The graphics card isn't drawn: OpenRGB doesn't detect its lighting, so it isn't part of the setup.
 */
function addDecor(root) {
  const [W, H, D] = caseSize;
  const decor = new THREE.Group();
  const dark = new THREE.MeshStandardMaterial({ color: 0x1b1c20, roughness: 0.6, metalness: 0.3 });

  // PSU shroud along the bottom, open under the fan column.
  const { shroudD, shroudZ } = shroudSpan();
  decor.add(at(box(W - 10, SHROUD_H, shroudD, dark), 0, SHROUD_H / 2, shroudZ));
  if (!parts.some((p) => !p.desk && p.role === 'logo')) {
    const bar = led({ source: AMBIENT, start: 0, span: 1 }, 0, 1, new THREE.TubeGeometry(new Stadium(shroudD - 90, 9), 160, 1.8, 6, true));
    decor.add(at(bar, -(W - 10) / 2 - 2, SHROUD_H * 0.45, shroudZ - 10));
  }

  // Fan places with no device on them yet (fans chained on a header that isn't split into fans).
  for (const t of spareFans) {
    const f = fanModel({ source: AMBIENT, start: 0, span: 1, leds: 12 });
    applyT(f, t);
    decor.add(f);
  }

  // The LEDs light the inside of the case.
  glow = new THREE.PointLight(0xffffff, 0, Math.max(W, H, D) * 1.3, 0);
  glow.position.set(-W / 4, H * 0.55, 0);
  decor.add(glow);
  root.add(decor);
}

/** The case's main colour right now: the average of its lit LEDs (hex), or null when they are all off. */
function ambientHex(sources) {
  let r = 0, g = 0, b = 0, n = 0;
  const seen = new Set();
  for (const p of parts) {
    if (p.desk || seen.has(p.source)) { continue; }
    seen.add(p.source);
    const c = sources[p.source];
    if (!Array.isArray(c)) { continue; }
    for (const hex of c) {
      if (typeof hex !== 'string' || !/^[0-9a-fA-F]{6}$/.test(hex)) { continue; }
      const v = parseInt(hex, 16), cr = v >> 16, cg = (v >> 8) & 255, cb = v & 255;
      if (cr + cg + cb < 24) { continue; }
      r += cr; g += cg; b += cb; n++;
    }
  }
  if (!n) { return null; }
  // Average, then bring it back to full brightness so a mix of the same hue doesn't look dim.
  const m = Math.max(r, g, b) / n || 1, k = Math.min(255 / m, 1.6);
  const to = (v) => Math.min(255, Math.round((v / n) * k)).toString(16).padStart(2, '0');
  return to(r) + to(g) + to(b);
}

// ---------- building the scene ----------

function register(id, o, def) {
  o.userData.id = id;
  objects.set(id, o);
  defaults.set(id, def);
  applyT(o, finite(overrides[id], 6) ? overrides[id] : def);
}

function build() {
  tc.detach();
  hideGuides();
  if (world) { scene.remove(world); disposeTree(world); }
  clearHelpers();
  pal = palette();
  scene.background = new THREE.Color(pal.bg);
  document.body.className = theme.dark ? 'dark' : 'light';
  leds = [];
  objects.clear();
  defaults.clear();
  partInfo.clear();
  pickRoots = [];
  mats = makeMats();
  world = new THREE.Group();
  scene.add(world);
  const [W, H, D] = caseSize;

  rootCase = new THREE.Group();
  shell = at(box(W, H, D, new THREE.MeshStandardMaterial({ color: pal.shell, transparent: true, opacity: 0.08, depthWrite: false, side: THREE.DoubleSide })), 0, H / 2, 0);
  rootCase.add(shell);
  const edges = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.BoxGeometry(W, H, D)), new THREE.LineBasicMaterial({ color: pal.edge, transparent: true, opacity: 0.85 }));
  edges.position.y = H / 2;
  rootCase.add(edges);
  // The window side panel (-X) gets a faint tint; feet underneath.
  rootCase.add(at(new THREE.Mesh(new THREE.PlaneGeometry(D - 30, H - 30), new THREE.MeshBasicMaterial({ color: pal.shell, transparent: true, opacity: 0.05, depthWrite: false, side: THREE.DoubleSide })), -W / 2 - 0.5, H / 2, 0));
  rootCase.children[rootCase.children.length - 1].rotation.y = Math.PI / 2;
  for (const [x, z] of [[-W / 2 + 25, -D / 2 + 30], [W / 2 - 25, -D / 2 + 30], [-W / 2 + 25, D / 2 - 30], [W / 2 - 25, D / 2 - 30]]) {
    rootCase.add(at(box(30, 8, 40, mats.leg), x, -4 + 0.01, z));
  }
  world.add(rootCase);

  rootDesk = new THREE.Group();
  deskTop = at(box(DESK_W, DESK_T, DESK_D, mats.desk), 0, -DESK_T / 2, 0);
  rootDesk.add(deskTop);
  for (const [x, z] of [[-DESK_W / 2 + 40, -DESK_D / 2 + 40], [DESK_W / 2 - 40, -DESK_D / 2 + 40], [-DESK_W / 2 + 40, DESK_D / 2 - 40], [DESK_W / 2 - 40, DESK_D / 2 - 40]]) {
    rootDesk.add(at(box(40, LEG_H, 40, mats.leg), x, -DESK_T - LEG_H / 2, z));
  }
  world.add(rootDesk);

  // The desk's top is at y = 0; the case stands on it on its 8 mm feet.
  register('@case', rootCase, [0, 8, 0, 0, 0, 0]);
  register('@desk', rootDesk, [600, 0, 40, 0, 0, 0]);
  const places = defaultPlaces();
  addDecor(rootCase);
  for (const p of parts) {
    partInfo.set(p.id, p);
    const o = makePart(p);
    o.userData.ptype = p.type;
    (p.desk ? rootDesk : rootCase).add(o);
    register(p.id, o, places.get(p.id));
    pickRoots.push(o);
  }
  editSel = editSel.filter((id) => objects.has(id));
  $('empty').hidden = parts.length > 0;
  applyColors(lastSources);
  attachGizmo();
  showSelection();
  updatePanel();
  requestRender();
}

// ---------- colours ----------

function applyColors(sources) {
  const amb = ambientHex(sources);
  for (const l of leds) {
    let hex;
    if (l.src === AMBIENT) {
      if (!amb) {
        if (l.hex !== null) { l.hex = null; l.mat.color.set(pal.unlit); l.glows.forEach((m) => m.color.set(0x000000)); }
        continue;
      }
      hex = amb;
    } else {
      const c = sources[l.src];
      if (!Array.isArray(c) || !c.length) { continue; }
      hex = c[Math.min(c.length - 1, Math.max(0, Math.floor(l.pos * c.length)))];
    }
    if (hex !== l.hex && typeof hex === 'string' && /^[0-9a-fA-F]{6}$/.test(hex)) {
      l.hex = hex;
      l.mat.color.set('#' + hex);
      for (const m of l.glows) { m.color.set('#' + hex); }
    }
  }
  if (glow) {
    if (amb) { glow.color.set('#' + amb); glow.intensity = 2.2; } else { glow.intensity = 0; }
  }
  requestRender();
}

// ---------- selection ----------

function clearHelpers() {
  for (const h of helpers) { scene.remove(h); h.geometry.dispose(); h.material.dispose(); }
  helpers = [];
}

function showSelection() {
  clearHelpers();
  // The app highlights targets (device / zone / fan ids); a part drawn for a piece of a zone (the parts of
  // a chained header that isn't split into fans) shows when its zone is the target.
  const ids = arrange ? editSel : [...hostSel].flatMap((id) => (id === 'all' ? ['@case']
    : [id, ...parts.filter((p) => p.target === id && p.id !== id).map((p) => p.id)]));
  const primary = editSel[editSel.length - 1];
  for (const id of ids) {
    const o = objects.get(id);
    if (!o) { continue; }
    const h = new THREE.BoxHelper(o, new THREE.Color(theme.accent));
    h.material.depthTest = false;
    h.material.transparent = true;
    h.material.opacity = arrange && id !== primary ? 0.55 : 1;
    h.renderOrder = 5;
    helpers.push(h);
    scene.add(h);
  }
  requestRender();
}

function nameOf(id) {
  if (id === '@case') { return 'Case'; }
  if (id === '@desk') { return 'Desk'; }
  const p = partInfo.get(id);
  return p ? `${p.name} (${p.typeLabel})` : id;
}

/** Objects in the arrange selection whose parent isn't also selected (those move with it). */
function topLevel(ids) {
  const objs = ids.map((id) => objects.get(id)).filter(Boolean);
  return objs.filter((o) => {
    for (let a = o.parent; a; a = a.parent) { if (objs.includes(a)) { return false; } }
    return true;
  });
}

function attachGizmo() {
  const primary = arrange ? objects.get(editSel[editSel.length - 1]) : null;
  if (primary) { tc.attach(primary); } else { tc.detach(); }
  tc.setMode(prefs.mode === 'rotate' ? 'rotate' : 'translate');
  tc.setTranslationSnap(prefs.grid ? prefs.gridSize : null);
  tc.setRotationSnap(prefs.grid ? 15 * DEG : null);
  requestRender();
}

function select(id, add) {
  if (!id) {
    if (!add) { editSel = []; }
  } else if (add) {
    const i = editSel.indexOf(id);
    if (i >= 0) { editSel.splice(i, 1); } else { editSel.push(id); }
  } else {
    editSel = [id];
  }
  attachGizmo();
  showSelection();
  updatePanel();
}

// ---------- picking and pointer ----------

const raycaster = new THREE.Raycaster();
function pickAt(ev) {
  const r = canvas.getBoundingClientRect();
  const ndc = new THREE.Vector2(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
  raycaster.setFromCamera(ndc, camera);
  const hit = raycaster.intersectObjects(pickRoots, true)[0];
  if (hit) {
    for (let o = hit.object; o; o = o.parent) {
      if (o.userData.id && o !== rootCase && o !== rootDesk) { return o.userData.id; }
    }
  }
  if (shell && raycaster.intersectObject(shell, false).length) { return '@case'; }
  if (rootDesk && raycaster.intersectObjects(rootDesk.children.filter((c) => !c.userData.id), false).length) { return '@desk'; }
  return null;
}

let down = null;
canvas.addEventListener('pointerdown', (e) => {
  down = { x: e.clientX, y: e.clientY, button: e.button, gizmo: !!(tc.object && tc.axis) };
  tip.hidden = true;
  canvas.focus();
});
canvas.addEventListener('pointerup', (e) => {
  const d = down;
  down = null;
  if (!d || d.gizmo || d.button !== e.button || Math.hypot(e.clientX - d.x, e.clientY - d.y) > 5) { return; }
  const id = pickAt(e);
  if (e.button === 0) {
    if (arrange) {
      select(id, e.shiftKey);
      if (id && !id.startsWith('@')) { post({ type: 'select', id, add: e.shiftKey }); }
    } else if (id === '@case') {
      post({ type: 'select', id: 'all', add: false });
    } else if (id && !id.startsWith('@')) {
      post({ type: 'select', id, add: e.shiftKey });
    }
  } else if (e.button === 2 && arrange && id) {
    if (!editSel.includes(id)) { select(id, false); }
    post({ type: 'context', id, x: e.clientX, y: e.clientY });
  } else if (e.button === 2 && !arrange && id && !id.startsWith('@')) {
    // Outside Arrange a right-click on a part offers to hide its device.
    post({ type: 'context', id, x: e.clientX, y: e.clientY });
  }
});
canvas.addEventListener('contextmenu', (e) => e.preventDefault());

/** Double-click: turn around the part under the pointer (or the whole setup), keeping the distance. */
canvas.addEventListener('dblclick', (e) => {
  if (e.button !== 0 || !world) { return; }
  const id = pickAt(e);
  const o = id ? objects.get(id) : null;
  if (!o || id === '@desk') { setView('iso'); return; }
  const c = boxOf(o).getCenter(new V3());
  camera.position.add(c.clone().sub(orbit.target));
  orbit.target.copy(c);
  orbit.update();
  saveCameraSoon();
  requestRender();
});
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => e.preventDefault());

let hoverQueued = false;
canvas.addEventListener('pointermove', (e) => {
  if (e.buttons || tc.dragging || hoverQueued) { return; }
  hoverQueued = true;
  requestAnimationFrame(() => {
    hoverQueued = false;
    const id = pickAt(e);
    if (!id || (id === '@desk' && !arrange)) { tip.hidden = true; return; }
    tip.textContent = nameOf(id);
    tip.hidden = false;
    const r = canvas.getBoundingClientRect();
    tip.style.left = Math.min(e.clientX - r.left + 14, r.width - tip.offsetWidth - 4) + 'px';
    tip.style.top = Math.min(e.clientY - r.top + 16, r.height - tip.offsetHeight - 4) + 'px';
  });
});
canvas.addEventListener('pointerleave', () => { tip.hidden = true; });

// ---------- editing with undo ----------

function snapshot(ids) {
  const o = {};
  for (const id of ids) { o[id] = finite(overrides[id], 6) ? overrides[id].slice() : null; }
  return o;
}

/** Store what changed since <before> (and push it for undo). */
function commit(before) {
  const ids = Object.keys(before).filter((id) => objects.has(id));
  const after = {};
  let changed = false;
  for (const id of ids) {
    const o = objects.get(id);
    const t = readT(o);
    applyT(o, t);
    const prev = before[id] || defaults.get(id);
    if (!sameT(t, prev)) { changed = true; }
    after[id] = t;
  }
  if (!changed) { updatePanel(); return; }
  for (const id of ids) { overrides[id] = after[id]; }
  undoStack.push({ before, after });
  if (undoStack.length > 200) { undoStack.shift(); }
  redoStack.length = 0;
  post({ type: 'transforms', items: after });
  updatePanel();
  requestRender();
}

function edit(fn) {
  const ids = editSel.filter((id) => objects.has(id));
  if (!ids.length) { return; }
  const before = snapshot(ids);
  fn(topLevel(ids));
  commit(before);
}

function applyState(state) {
  const items = {};
  for (const id of Object.keys(state)) {
    const o = objects.get(id);
    if (!o) { continue; }
    const t = state[id];
    if (t) { overrides[id] = t.slice(); applyT(o, t); } else { delete overrides[id]; applyT(o, defaults.get(id)); }
    items[id] = t;
  }
  post({ type: 'transforms', items });
  updatePanel();
  showSelection();
}

function undo() { const e = undoStack.pop(); if (e) { applyState(e.before); redoStack.push(e); updatePanel(); } }
function redo() { const e = redoStack.pop(); if (e) { applyState(e.after); undoStack.push(e); updatePanel(); } }

// ---------- dragging with the gizmo: multi-selection followers and snapping ----------

function dragStart() {
  const ids = editSel.filter((id) => objects.has(id));
  const tops = topLevel(ids);
  drag = { before: snapshot(ids), start: new Map(), tops, cands: [] };
  for (const o of tops) { drag.start.set(o, { pos: o.getWorldPosition(new V3()), quat: o.getWorldQuaternion(new Q()) }); }
  const moving = (o) => { for (let a = o; a; a = a.parent) { if (tops.includes(a)) { return true; } } return false; };
  for (const [id, o] of objects) {
    if (!id.startsWith('@') && !moving(o)) { drag.cands.push(boxOf(o)); }
  }
  if (!moving(shell)) { drag.cands.push(boxOf(shell)); }
  if (!moving(deskTop)) {
    const b = boxOf(deskTop);
    drag.cands.push(b);
  }
}

function dragEnd() {
  hideGuides();
  if (drag) { const before = drag.before; drag = null; commit(before); }
  showSelection();
}

function onDrag() {
  const p = tc.object;
  if (!drag || !p || !drag.start.has(p)) { updatePanel(); return; }
  const st = drag.start.get(p);
  hideGuides();
  if (tc.mode === 'translate') {
    let faced = false;
    if (prefs.snapParts && p.parent === rootCase && p.userData.ptype === 'fan') { faced = snapToFace(p); }
    if (!faced) {
      setWorldQuat(p, st.quat.clone());
      if (prefs.snapParts) { snapToParts(p); }
    }
    const d = p.getWorldPosition(new V3()).sub(st.pos);
    for (const o of drag.tops) {
      if (o !== p) { setWorldPos(o, drag.start.get(o).pos.clone().add(d)); }
    }
  } else {
    const dq = p.getWorldQuaternion(new Q()).multiply(st.quat.clone().invert());
    for (const o of drag.tops) {
      if (o !== p) { setWorldQuat(o, dq.clone().multiply(drag.start.get(o).quat)); }
    }
  }
  for (const h of helpers) { h.update(); }
  updatePanel(true);
}

/** A fan dragged near a case face lands on it, turned to face that way, and clicks into the nearest slot. */
function snapToFace(p) {
  const lp = rootCase.worldToLocal(p.getWorldPosition(new V3()));
  let best = null;
  for (const face of FACES) {
    const f = faceSlots(face);
    const dist = Math.abs(lp[f.axis] - f.value);
    if (dist < 35 && (!best || dist < best.dist)) { best = { f, dist }; }
  }
  if (!best) { return false; }
  lp[best.f.axis] = best.f.value;
  let slot = null, sd = 30;
  for (const s of best.f.slots) {
    const d = lp.distanceTo(new V3(...s));
    if (d < sd) { sd = d; slot = s; }
  }
  if (slot) { lp.set(...slot); }
  p.position.copy(lp);
  p.rotation.set(best.f.rot[0] * DEG, best.f.rot[1] * DEG, best.f.rot[2] * DEG);
  p.updateMatrixWorld(true);
  slotGuide.position.copy(rootCase.localToWorld(lp.clone()));
  slotGuide.quaternion.copy(rootCase.getWorldQuaternion(new Q()).multiply(new Q().setFromEuler(p.rotation)));
  slotGuide.material.color.set(slot ? 0x33d17a : 0xffc04d);
  slotGuide.visible = true;
  return true;
}

/** Line the dragged part's edges or centre up with another part's (or the case's / desk's). */
function snapToParts(p) {
  const b = boxOf(p);
  const dist = camera.position.distanceTo(orbit.target);
  const thr = Math.min(25, Math.max(3, dist * 0.008));
  const vals = (bx, ax) => [bx.min[ax], (bx.min[ax] + bx.max[ax]) / 2, bx.max[ax]];
  const delta = new V3();
  const found = [];
  for (const ax of AXES) {
    if (tc.axis && !tc.axis.includes(ax.toUpperCase())) { continue; }
    let best = null;
    const mine = vals(b, ax);
    for (const c of drag.cands) {
      for (const t of vals(c, ax)) {
        for (const m of mine) {
          const d = t - m;
          if (Math.abs(d) < thr && (!best || Math.abs(d) < Math.abs(best.d))) { best = { d, value: t, c }; }
        }
      }
    }
    if (best) { delta[ax] = best.d; found.push({ ax, value: best.value, c: best.c }); }
  }
  if (!found.length) { return; }
  moveWorld(p, delta);
  const mb = boxOf(p);
  const pos = guideGeo.attributes.position, col = guideGeo.attributes.color;
  found.forEach((g, i) => {
    const a = mb.getCenter(new V3()), c = g.c.getCenter(new V3());
    a[g.ax] = g.value;
    c[g.ax] = g.value;
    pos.setXYZ(i * 2, a.x, a.y, a.z);
    pos.setXYZ(i * 2 + 1, c.x, c.y, c.z);
    const k = AXIS_COLORS[g.ax];
    col.setXYZ(i * 2, k.r, k.g, k.b);
    col.setXYZ(i * 2 + 1, k.r, k.g, k.b);
  });
  guideGeo.setDrawRange(0, found.length * 2);
  pos.needsUpdate = true;
  col.needsUpdate = true;
  guideGeo.computeBoundingSphere();
  guides.visible = true;
}

function hideGuides() {
  guides.visible = false;
  slotGuide.visible = false;
}

// ---------- alignment tools ----------

/** What a single selected part aligns to: the inside of the case, or the desk's top. */
function containerBox(o) {
  if (o.parent === rootCase) { return boxOf(shell); }
  if (o.parent === rootDesk) {
    const b = boxOf(deskTop);
    b.min.y = b.max.y;
    return b;
  }
  return null;
}

function referenceBox(objs) {
  if (objs.length >= 2) {
    const ref = new THREE.Box3();
    objs.forEach((o) => ref.union(boxOf(o)));
    return ref;
  }
  return objs.length ? containerBox(objs[0]) : null;
}

function align(side) {
  edit((objs) => {
    const ref = referenceBox(objs);
    if (!ref) { return; }
    for (const o of objs) {
      const b = boxOf(o), d = new V3();
      switch (side) {
        case 'left': d.x = ref.min.x - b.min.x; break;
        case 'right': d.x = ref.max.x - b.max.x; break;
        case 'bottom': d.y = ref.min.y - b.min.y; break;
        case 'top': d.y = ref.max.y - b.max.y; break;
        case 'back': d.z = ref.min.z - b.min.z; break;
        case 'front': d.z = ref.max.z - b.max.z; break;
      }
      moveWorld(o, d);
    }
  });
}

function center(ax) {
  edit((objs) => {
    const ref = referenceBox(objs);
    if (!ref) { return; }
    const target = ref.getCenter(new V3())[ax];
    for (const o of objs) {
      const d = new V3();
      d[ax] = target - boxOf(o).getCenter(new V3())[ax];
      moveWorld(o, d);
    }
  });
}

function distribute(ax) {
  edit((objs) => {
    if (objs.length < 3) { return; }
    const items = objs.map((o) => ({ o, c: boxOf(o).getCenter(new V3())[ax] })).sort((a, b) => a.c - b.c);
    const first = items[0].c, step = (items[items.length - 1].c - first) / (items.length - 1);
    items.forEach((it, i) => {
      const d = new V3();
      d[ax] = first + step * i - it.c;
      moveWorld(it.o, d);
    });
  });
}

function rotateBy(ax, deg) {
  const axis = new V3(ax === 'x' ? 1 : 0, ax === 'y' ? 1 : 0, ax === 'z' ? 1 : 0);
  edit((objs) => {
    const q = new Q().setFromAxisAngle(axis, deg * DEG);
    for (const o of objs) { setWorldQuat(o, q.clone().multiply(o.getWorldQuaternion(new Q()))); }
  });
}

function sameRotation() {
  const primary = objects.get(editSel[editSel.length - 1]);
  if (!primary || editSel.length < 2) { return; }
  const wq = primary.getWorldQuaternion(new Q());
  edit((objs) => { for (const o of objs) { if (o !== primary) { setWorldQuat(o, wq.clone()); } } });
}

function toFace(face) {
  const f = faceSlots(face);
  edit((objs) => {
    const inCase = objs.filter((o) => o.parent === rootCase);
    const free = f.slots.map((s) => new V3(...s));
    for (const o of inCase) {
      let best = 0;
      if (free.length > 1) {
        let bd = Infinity;
        free.forEach((s, i) => { const d = s.distanceTo(o.position); if (d < bd) { bd = d; best = i; } });
      }
      const slot = free.length > 1 ? free.splice(best, 1)[0] : free[0];
      o.position.copy(slot);
      o.rotation.set(f.rot[0] * DEG, f.rot[1] * DEG, f.rot[2] * DEG);
      o.updateMatrixWorld(true);
    }
  });
}

function nudge(dx, dy, dz) {
  edit((objs) => { for (const o of objs) { moveWorld(o, new V3(dx, dy, dz)); } });
}

// ---------- camera ----------

function setView(name) {
  if (!world) { return; }
  let b;
  let dir;
  const cq = rootCase.getWorldQuaternion(new Q());
  switch (name) {
    case 'front': b = boxOf(rootCase); dir = new V3(0, 0, 1).applyQuaternion(cq); break;
    case 'top': b = boxOf(rootCase); dir = new V3(0, 1, 0.002).applyQuaternion(cq); break;
    case 'side': b = boxOf(rootCase); dir = new V3(-1, 0, 0).applyQuaternion(cq); break;
    default:
      // The whole setup, without the desk's legs.
      b = boxOf(rootCase).union(boxOf(deskTop));
      rootDesk.children.forEach((c) => { if (c.userData.id) { b.union(boxOf(c)); } });
      dir = new V3(-1, 0.85, 1.1);
      break;
  }
  if (b.isEmpty()) { return; }
  const c = b.getCenter(new V3());
  const radius = b.getBoundingSphere(new THREE.Sphere()).radius;
  let dist = radius / Math.sin((camera.fov * DEG) / 2) * 1.02;
  if (camera.aspect < 1) { dist /= camera.aspect; }
  camera.position.copy(c).add(dir.normalize().multiplyScalar(dist));
  camera.up.set(0, 1, 0);
  orbit.target.copy(c);
  orbit.update();
  hasCamera = true;
  saveCameraSoon();
  requestRender();
}

let cameraTimer = 0;
function saveCameraSoon() {
  clearTimeout(cameraTimer);
  cameraTimer = setTimeout(() => {
    const p = camera.position, t = orbit.target;
    post({ type: 'camera', value: [p.x, p.y, p.z, t.x, t.y, t.z].map(r1) });
  }, 600);
}

// ---------- panel ----------

const panelInputs = { px: 0, py: 1, pz: 2, rx: 3, ry: 4, rz: 5 };

function updatePanel(live) {
  $('panel').hidden = !arrange;
  $('hint').textContent = arrange
    ? 'Left-drag: rotate view · Right-drag: move view · Wheel / middle-drag: zoom · Click a part to select it, drag its arrows to move it · Right-click a part to set its type'
    : 'Left-drag: rotate · Right-drag: move · Wheel / middle-drag: zoom · Double-click: focus · Click a part to select it (Shift adds) · Right-click: hide';
  if (!arrange) { return; }
  const primary = objects.get(editSel[editSel.length - 1]);
  $('selName').textContent = editSel.length === 0 ? 'Nothing selected. Click a part, the case or the desk.'
    : editSel.length === 1 ? nameOf(editSel[0]) : `${editSel.length} selected (gizmo on ${nameOf(editSel[editSel.length - 1])})`;
  const t = primary ? readT(primary) : null;
  for (const [id, i] of Object.entries(panelInputs)) {
    const el = $(id);
    el.disabled = !primary;
    if (document.activeElement !== el || live) { el.value = t ? String(t[i]) : ''; }
  }
  const n = editSel.length;
  document.querySelectorAll('[data-need]').forEach((el) => { el.disabled = n < Number(el.dataset.need); });
  const caseParts = editSel.some((id) => objects.get(id) && objects.get(id).parent === rootCase);
  document.querySelectorAll('[data-action="face"]').forEach((el) => { el.disabled = !caseParts; });
  $('btnUndo').disabled = !undoStack.length;
  $('btnRedo').disabled = !redoStack.length;
  $('btnMove').setAttribute('aria-pressed', String(tc.mode === 'translate'));
  $('btnRotate').setAttribute('aria-pressed', String(tc.mode === 'rotate'));
  $('btnSpace').textContent = tc.space === 'local' ? 'Own axes' : 'World axes';
  $('gridOn').checked = prefs.grid;
  if (document.activeElement !== $('gridSize')) { $('gridSize').value = String(prefs.gridSize); }
  $('snapParts').checked = prefs.snapParts;
  ['cw', 'ch', 'cd'].forEach((id, i) => { if (document.activeElement !== $(id)) { $(id).value = String(caseSize[i]); } });
}

document.addEventListener('click', (e) => {
  const b = e.target.closest('button[data-action]');
  if (!b || b.disabled) { return; }
  const a = b.dataset;
  switch (a.action) {
    case 'view': setView(a.view); break;
    case 'mode': prefs.mode = a.mode; attachGizmo(); updatePanel(); break;
    case 'space': tc.setSpace(tc.space === 'local' ? 'world' : 'local'); updatePanel(); break;
    case 'undo': undo(); break;
    case 'redo': redo(); break;
    case 'rot': rotateBy(a.axis, Number(a.deg)); break;
    case 'samerot': sameRotation(); break;
    case 'align': align(a.side); break;
    case 'center': center(a.axis); break;
    case 'dist': distribute(a.axis); break;
    case 'face': toFace(a.face); break;
  }
  requestRender();
});

for (const [id, i] of Object.entries(panelInputs)) {
  $(id).addEventListener('change', () => {
    const o = objects.get(editSel[editSel.length - 1]);
    const v = Number($(id).value);
    if (!o || !Number.isFinite(v)) { updatePanel(); return; }
    const before = snapshot([o.userData.id]);
    const t = readT(o);
    t[i] = i < 3 ? Math.max(-10000, Math.min(10000, v)) : v;
    applyT(o, t);
    commit(before);
    showSelection();
  });
}

$('gridOn').addEventListener('change', () => { prefs.grid = $('gridOn').checked; savePrefs(); attachGizmo(); });
$('gridSize').addEventListener('change', () => {
  const v = Number($('gridSize').value);
  if (Number.isFinite(v) && v >= 1 && v <= 500) { prefs.gridSize = v; savePrefs(); attachGizmo(); }
  updatePanel();
});
$('snapParts').addEventListener('change', () => { prefs.snapParts = $('snapParts').checked; savePrefs(); });
for (const [id, i] of [['cw', 0], ['ch', 1], ['cd', 2]]) {
  $(id).addEventListener('change', () => {
    const v = Number($(id).value);
    if (Number.isFinite(v) && v >= 150 && v <= 1200) {
      caseSize[i] = Math.round(v);
      post({ type: 'caseSize', value: caseSize.slice() });
      build();
    }
    updatePanel();
  });
}

// ---------- keyboard ----------

window.addEventListener('keydown', (e) => {
  const inInput = e.target && e.target.tagName === 'INPUT';
  const k = e.key;
  if (e.ctrlKey && !e.altKey) {
    if (inInput || !arrange) { return; }
    if (k === 'z' || k === 'Z') { e.preventDefault(); if (e.shiftKey) { redo(); } else { undo(); } }
    else if (k === 'y' || k === 'Y') { e.preventDefault(); redo(); }
    return;
  }
  if (inInput || e.altKey || e.metaKey) { return; }
  switch (k) {
    case 'Home': e.preventDefault(); setView('iso'); return;
    case '1': setView('front'); return;
    case '2': setView('top'); return;
    case '3': setView('side'); return;
    case '4': setView('iso'); return;
  }
  if (!arrange) { return; }
  const lower = k.toLowerCase();
  if (lower === 'x' || lower === 'y' || lower === 'z') {
    const axis = lower.toUpperCase();
    if (tc.dragging) {
      tc.axis = axis;
    } else {
      const only = !(tc.showX && tc.showY && tc.showZ) && tc['show' + axis];
      tc.showX = only || axis === 'X';
      tc.showY = only || axis === 'Y';
      tc.showZ = only || axis === 'Z';
    }
    requestRender();
    return;
  }
  if (lower === 'm' || lower === 'g') { prefs.mode = 'translate'; attachGizmo(); updatePanel(); return; }
  if (lower === 'r') { prefs.mode = 'rotate'; attachGizmo(); updatePanel(); return; }
  if (k === 'Escape') { select(null, false); return; }
  const rotating = tc.mode === 'rotate';
  const step = rotating ? (e.shiftKey ? 90 : prefs.grid ? 15 : 5) : (prefs.grid ? prefs.gridSize : 1) * (e.shiftKey ? 10 : 1);
  const moves = {
    ArrowLeft: ['x', -1], ArrowRight: ['x', 1], ArrowUp: ['z', -1], ArrowDown: ['z', 1], PageUp: ['y', 1], PageDown: ['y', -1],
  };
  const m = moves[k];
  if (!m || !editSel.length) { return; }
  e.preventDefault();
  if (rotating) {
    const rotAxis = { ArrowLeft: 'y', ArrowRight: 'y', ArrowUp: 'x', ArrowDown: 'x', PageUp: 'z', PageDown: 'z' }[k];
    rotateBy(rotAxis, m[1] * step);
  } else {
    nudge(m[0] === 'x' ? m[1] * step : 0, m[0] === 'y' ? m[1] * step : 0, m[0] === 'z' ? m[1] * step : 0);
  }
  showSelection();
});

// ---------- messages from the app ----------

function onMessage(msg) {
  if (!msg || typeof msg !== 'object' || typeof msg.type !== 'string') { return; }
  switch (msg.type) {
    case 'init': {
      theme = { dark: !!msg.dark, accent: typeof msg.accent === 'string' && /^#[0-9a-fA-F]{6}$/.test(msg.accent) ? msg.accent : theme.accent };
      parts = Array.isArray(msg.parts) ? msg.parts.filter((p) => p && typeof p.id === 'string') : [];
      overrides = {};
      if (msg.layout && typeof msg.layout === 'object') {
        for (const [id, t] of Object.entries(msg.layout)) { if (finite(t, 6)) { overrides[id] = t.slice(); } }
      }
      if (finite(msg.caseSize, 3)) { caseSize = msg.caseSize.slice(); }
      arrange = !!msg.arrange;
      hostSel = new Set(Array.isArray(msg.highlight) ? msg.highlight : []);
      undoStack.length = 0;
      redoStack.length = 0;
      build();
      if (!(msg.keepCamera && hasCamera)) {
        if (finite(msg.camera, 6)) {
          camera.position.set(msg.camera[0], msg.camera[1], msg.camera[2]);
          orbit.target.set(msg.camera[3], msg.camera[4], msg.camera[5]);
          orbit.update();
          hasCamera = true;
        } else {
          setView('iso');
        }
      }
      requestRender();
      break;
    }
    case 'colors':
      if (msg.sources && typeof msg.sources === 'object') { lastSources = msg.sources; applyColors(lastSources); }
      break;
    case 'highlight':
      hostSel = new Set(Array.isArray(msg.ids) ? msg.ids.filter((id) => typeof id === 'string') : []);
      if (!arrange) { showSelection(); }
      break;
    case 'arrange':
      arrange = !!msg.on;
      if (!arrange) { editSel = []; tc.showX = tc.showY = tc.showZ = true; }
      attachGizmo();
      showSelection();
      updatePanel();
      break;
    case 'theme':
      theme.dark = !!msg.dark;
      build();
      break;
  }
}

if (webview) {
  webview.addEventListener('message', (e) => onMessage(e.data));
} else {
  // Opened in a plain browser (for development): let the console drive it.
  window.lightsyncPreviewMessage = onMessage;}

// ---------- sizing and drawing ----------

function resize() {
  const w = Math.max(1, canvas.clientWidth), h = Math.max(1, canvas.clientHeight);
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  requestRender();
}
new ResizeObserver(resize).observe(canvas);
resize();

function frame() {
  requestAnimationFrame(frame);
  if (!dirty) { return; }
  dirty = false;
  for (const h of helpers) { h.update(); }
  renderer.render(scene, camera);
}
requestAnimationFrame(frame);
updatePanel();
post({ type: 'ready' });

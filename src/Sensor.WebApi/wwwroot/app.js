// Minimal live view: subscribes to GET /api/events (Server-Sent Events) and renders each "status" event.
"use strict";

const IMAGE_SIZE = 10; // Fixed by the protocol: 10x10 pixels, RGBA, row-major, top row first.
const $ = (id) => document.getElementById(id);

/** Shows a value in a badge-like element with a state-colored CSS class. */
function setBadge(id, value) {
  const el = $(id);
  el.textContent = value ?? "—";
  el.className = (el.classList.contains("badge") ? "badge " : "") + (value ? `s-${value}` : "");
}

const ms = (value) => (value == null ? "—" : `${Math.round(value)} ms`);

/** Draws a 10x10 RGBA image scaled to the canvas, and an optional outlined pointer pixel. */
function drawImage(canvasId, image, position) {
  const canvas = $(canvasId);
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (!image) return;

  const bytes = Uint8ClampedArray.from(atob(image.rgbaBase64), (c) => c.charCodeAt(0));
  const pixels = new ImageData(bytes, image.width, image.height);
  const offscreen = new OffscreenCanvas(image.width, image.height);
  offscreen.getContext("2d").putImageData(pixels, 0, 0);
  ctx.imageSmoothingEnabled = false;
  ctx.drawImage(offscreen, 0, 0, canvas.width, canvas.height);

  if (position) {
    const cell = canvas.width / IMAGE_SIZE;
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#fff";
    ctx.strokeRect(position.x * cell + 1.5, position.y * cell + 1.5, cell - 3, cell - 3);
    ctx.lineWidth = 1;
    ctx.strokeStyle = "#000";
    ctx.strokeRect(position.x * cell, position.y * cell, cell, cell);
  }
}

function renderSensor(prefix, sensor) {
  setBadge(`${prefix}-state`, sensor.state);
  setBadge(`${prefix}-connection`, sensor.connection);
  $(`${prefix}-seen`).textContent = sensor.lastSeenMsAgo == null ? "—" : `${ms(sensor.lastSeenMsAgo)} ago`;
  $(`${prefix}-session`).textContent = sensor.sessionId ?? "—";
}

function render(status) {
  setBadge("system-state", status.systemState);
  $("fault-reason").textContent = status.faultReason ?? "—";
  renderSensor("camera", status.camera);
  renderSensor("pointer", status.pointer);

  const pointer = status.pointer.latestPosition;
  drawImage("raw-image", status.camera.latestImage, pointer);
  $("raw-image-seq").textContent = status.camera.latestImage?.sequence ?? "—";
  $("raw-pointer").textContent = pointer ? `(${pointer.x}, ${pointer.y}) #${pointer.sequence}` : "—";

  const result = status.latestResult;
  if (result) {
    const { r, g, b, a } = result.color;
    $("result-swatch").style.background = `rgba(${r}, ${g}, ${b}, ${a / 255})`;
    $("result-rgba").textContent = `R ${r}  G ${g}  B ${b}  A ${a}`;
    drawImage("result-image", result.image, result.position);
    $("result-image-seq").textContent = result.image.sequence;
    $("result-position").textContent = `(${result.position.x}, ${result.position.y})`;
    $("result-pointer-seq").textContent = result.pointerSequence;
    $("result-skew").textContent = Math.round(result.imageAgeAtPairingMs);
    $("result-age").textContent = ms(result.ageMs);
  }

  const p = status.processing;
  $("stats").textContent =
    `pairs ${p.pairsSubmitted} · processed ${p.pairsProcessed} · replaced ${p.pairsReplaced} · ` +
    `skipped: no image ${p.skippedNoImage}, stale image ${p.skippedStaleImage}`;
}

// EventSource reconnects automatically; the badge shows the stream state.
const events = new EventSource("/api/events");
events.addEventListener("status", (e) => render(JSON.parse(e.data)));
events.onopen = () => setBadge("stream", "live");
events.onerror = () => setBadge("stream", "down");

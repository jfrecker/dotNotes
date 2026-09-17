// Graph view (docs/03-FEATURE-SPEC.md "Should-have": graph visualizing
// note-to-note links). Renders `GET /api/graph` as a small hand-rolled
// force-directed layout on a <canvas>, rather than vendoring a graph
// library - CLAUDE.md's "no build step" and "keep DOM/JS simple" rules
// make a few dozen lines of physics a better fit here than pulling in
// (and keeping offline/pinned) something like d3 for a single view in a
// personal tool. See the simulation constants below for the whole thing:
// repulsion between every node pair, a spring along each edge, a gentle
// pull toward the canvas center, velocity damping, one Euler integration
// step per animation frame.
(() => {
  const canvas = document.getElementById('graph-canvas');
  const ctx = canvas.getContext('2d');
  const statusEl = document.getElementById('graph-status');
  const emptyMessageEl = document.getElementById('graph-empty-message');
  const refreshBtn = document.getElementById('graph-refresh-btn');

  // --- simulation tuning ----------------------------------------------------
  const REPULSION = 12000; // how strongly every pair of nodes pushes apart
  const SPRING_LENGTH = 110; // an edge's "resting" length
  const SPRING_STRENGTH = 0.02;
  const CENTER_PULL = 0.01; // gentle pull toward the canvas center, keeps disconnected nodes from drifting off-screen
  const DAMPING = 0.82;
  const SETTLE_SPEED = 0.03; // once every node's speed drops below this, stop animating until something changes
  const NODE_RADIUS = 7;
  const HIT_RADIUS = 14; // generous click target - bigger than the drawn dot
  const DRAG_CLICK_THRESHOLD_PX = 4; // pointer movement under this = a click, not a drag

  let nodes = []; // { id, label, exists, x, y, vx, vy }
  let edges = []; // { source, target } - direct references into `nodes`
  let nodesById = new Map();
  let animationHandle = null;
  let dragNode = null;
  let dragMoved = 0;
  let dragPointerId = null;

  function setStatus(text) {
    statusEl.textContent = text;
  }

  // --- data loading -----------------------------------------------------------

  async function loadGraph() {
    setStatus('Loading…');
    try {
      const graph = await Api.getGraph();
      buildSimulationState(graph);
      emptyMessageEl.classList.toggle('hidden', nodes.length > 0);
      emptyMessageEl.classList.toggle('flex', nodes.length === 0);
      setStatus(`${nodes.length} note${nodes.length === 1 ? '' : 's'}, ${edges.length} link${edges.length === 1 ? '' : 's'}`);
      wake();
    } catch (err) {
      setStatus(`Failed to load graph: ${err.message}`);
    }
  }

  /** Seeds node positions on a circle (so an initial layout isn't just a pile at the center) and rebuilds the id->node map + edge references. */
  function buildSimulationState(graph) {
    const rect = canvas.getBoundingClientRect();
    const centerX = rect.width / 2;
    const centerY = rect.height / 2;
    const seedRadius = Math.min(rect.width, rect.height) / 3;

    const previousPositions = nodesById; // reuse old positions across a manual refresh, keyed by id
    nodes = (graph.nodes || []).map((n, index) => {
      const previous = previousPositions.get(n.id);
      const angle = (index / Math.max(graph.nodes.length, 1)) * Math.PI * 2;
      return {
        id: n.id,
        label: n.label,
        exists: n.exists,
        x: previous ? previous.x : centerX + Math.cos(angle) * seedRadius,
        y: previous ? previous.y : centerY + Math.sin(angle) * seedRadius,
        vx: 0,
        vy: 0,
      };
    });

    nodesById = new Map(nodes.map((n) => [n.id, n]));

    edges = (graph.edges || [])
      .map((e) => ({ source: nodesById.get(e.source), target: nodesById.get(e.target) }))
      .filter((e) => e.source && e.target);
  }

  // --- physics step -----------------------------------------------------------

  function stepSimulation() {
    const rect = canvas.getBoundingClientRect();
    const centerX = rect.width / 2;
    const centerY = rect.height / 2;

    // Repulsion: every pair of nodes pushes apart, inverse-square falloff.
    for (let i = 0; i < nodes.length; i++) {
      for (let j = i + 1; j < nodes.length; j++) {
        const a = nodes[i];
        const b = nodes[j];
        let dx = a.x - b.x;
        let dy = a.y - b.y;
        let distSq = dx * dx + dy * dy;
        if (distSq < 1) {
          // Nodes started on top of each other - nudge apart deterministically-ish rather than dividing by ~0.
          dx = (i - j) || 1;
          dy = 1;
          distSq = dx * dx + dy * dy;
        }
        const dist = Math.sqrt(distSq);
        const force = REPULSION / distSq;
        const fx = (dx / dist) * force;
        const fy = (dy / dist) * force;
        a.vx += fx;
        a.vy += fy;
        b.vx -= fx;
        b.vy -= fy;
      }
    }

    // Attraction: each edge behaves like a spring toward SPRING_LENGTH.
    for (const edge of edges) {
      const dx = edge.target.x - edge.source.x;
      const dy = edge.target.y - edge.source.y;
      const dist = Math.sqrt(dx * dx + dy * dy) || 1;
      const force = (dist - SPRING_LENGTH) * SPRING_STRENGTH;
      const fx = (dx / dist) * force;
      const fy = (dy / dist) * force;
      edge.source.vx += fx;
      edge.source.vy += fy;
      edge.target.vx -= fx;
      edge.target.vy -= fy;
    }

    // Centering + damping + integration.
    let maxSpeed = 0;
    for (const node of nodes) {
      if (node === dragNode) {
        // The dragged node follows the pointer directly (see onPointerMove); don't let physics fight the drag.
        continue;
      }
      node.vx += (centerX - node.x) * CENTER_PULL;
      node.vy += (centerY - node.y) * CENTER_PULL;
      node.vx *= DAMPING;
      node.vy *= DAMPING;
      node.x += node.vx;
      node.y += node.vy;
      maxSpeed = Math.max(maxSpeed, Math.abs(node.vx), Math.abs(node.vy));
    }

    return maxSpeed;
  }

  // --- rendering ---------------------------------------------------------------

  function render() {
    const rect = canvas.getBoundingClientRect();
    ctx.clearRect(0, 0, rect.width, rect.height);

    ctx.strokeStyle = '#cbd5e1';
    ctx.lineWidth = 1;
    for (const edge of edges) {
      ctx.beginPath();
      ctx.moveTo(edge.source.x, edge.source.y);
      ctx.lineTo(edge.target.x, edge.target.y);
      ctx.stroke();
    }

    ctx.font = '12px system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    for (const node of nodes) {
      ctx.beginPath();
      ctx.arc(node.x, node.y, NODE_RADIUS, 0, Math.PI * 2);
      if (node.exists) {
        ctx.fillStyle = '#2563eb';
        ctx.setLineDash([]);
      } else {
        ctx.fillStyle = '#fef3c7';
        ctx.strokeStyle = '#b45309';
        ctx.setLineDash([2, 2]);
        ctx.lineWidth = 1.5;
      }
      ctx.fill();
      if (!node.exists) {
        ctx.stroke();
      }

      ctx.setLineDash([]);
      ctx.fillStyle = node.exists ? '#1e293b' : '#92400e';
      ctx.fillText(node.label, node.x + NODE_RADIUS + 4, node.y);
    }
  }

  // --- animation loop ------------------------------------------------------

  function tick() {
    const maxSpeed = stepSimulation();
    render();
    if (maxSpeed > SETTLE_SPEED || dragNode) {
      animationHandle = requestAnimationFrame(tick);
    } else {
      animationHandle = null; // settled - stop burning CPU until something wakes it back up
    }
  }

  /** (Re)starts the animation loop if it isn't already running. */
  function wake() {
    if (animationHandle === null) {
      animationHandle = requestAnimationFrame(tick);
    }
  }

  // --- canvas sizing --------------------------------------------------------

  function resizeCanvas() {
    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(rect.width * dpr);
    canvas.height = Math.round(rect.height * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    render();
  }

  window.addEventListener('resize', () => {
    resizeCanvas();
    wake();
  });

  // --- pointer interaction: click-to-navigate, drag-to-reposition ------------

  function pointerPosition(event) {
    const rect = canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  function findNodeAt(x, y) {
    // Iterate in reverse draw order so the visually topmost node wins on overlap.
    for (let i = nodes.length - 1; i >= 0; i--) {
      const node = nodes[i];
      const dx = node.x - x;
      const dy = node.y - y;
      if (dx * dx + dy * dy <= HIT_RADIUS * HIT_RADIUS) {
        return node;
      }
    }
    return null;
  }

  canvas.addEventListener('pointerdown', (event) => {
    const pos = pointerPosition(event);
    const node = findNodeAt(pos.x, pos.y);
    if (!node) {
      return;
    }
    dragNode = node;
    dragMoved = 0;
    dragPointerId = event.pointerId;
    canvas.setPointerCapture(event.pointerId);
    canvas.classList.replace('cursor-grab', 'cursor-grabbing');
  });

  canvas.addEventListener('pointermove', (event) => {
    if (!dragNode || event.pointerId !== dragPointerId) {
      return;
    }
    const pos = pointerPosition(event);
    dragMoved += Math.abs(pos.x - dragNode.x) + Math.abs(pos.y - dragNode.y);
    dragNode.x = pos.x;
    dragNode.y = pos.y;
    dragNode.vx = 0;
    dragNode.vy = 0;
    wake();
  });

  async function onNodeClicked(node) {
    if (node.exists) {
      window.location.href = `/index.html?note=${encodeURIComponent(node.id)}`;
      return;
    }
    const confirmed = window.confirm(`Note "${node.id}" doesn't exist yet. Create it?`);
    if (!confirmed) {
      return;
    }
    try {
      await Api.saveNote(node.id, '');
      window.location.href = `/index.html?note=${encodeURIComponent(node.id)}`;
    } catch (err) {
      window.alert(`Could not create note "${node.id}": ${err.message}`);
    }
  }

  canvas.addEventListener('pointerup', (event) => {
    if (!dragNode || event.pointerId !== dragPointerId) {
      return;
    }
    const clickedNode = dragMoved < DRAG_CLICK_THRESHOLD_PX ? dragNode : null;
    canvas.releasePointerCapture(event.pointerId);
    canvas.classList.replace('cursor-grabbing', 'cursor-grab');
    dragNode = null;
    dragPointerId = null;
    if (clickedNode) {
      onNodeClicked(clickedNode);
    }
  });

  // --- wiring ----------------------------------------------------------------

  refreshBtn.addEventListener('click', loadGraph);

  resizeCanvas();
  loadGraph();
})();

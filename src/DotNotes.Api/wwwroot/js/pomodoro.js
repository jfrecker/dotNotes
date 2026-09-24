// Pure client-side Pomodoro timer (docs/features/tasks-kanban/PLAN.md §5/
// §9) - never writes to task files, "linking" a task just labels the
// session for the person using it. State is keyed on an absolute `endsAt`
// timestamp in localStorage so a running timer survives in-app navigation
// and full page reloads without drifting (a countdown re-derived from
// `endsAt - Date.now()` on every tick is immune to setInterval jitter or
// the tab being backgrounded/throttled).
const Pomodoro = (() => {
  const SETTINGS_KEY = 'dotnotes-pomodoro-settings';
  const STATE_KEY = 'dotnotes-pomodoro-state';

  const PHASES = ['focus', 'shortBreak', 'longBreak'];
  const PHASE_LABELS = { focus: 'Focus', shortBreak: 'Short break', longBreak: 'Long break' };

  const DEFAULT_SETTINGS = {
    workMin: 25,
    shortBreakMin: 5,
    longBreakMin: 15,
    cyclesBeforeLong: 4,
    soundOn: true,
    notifyOn: false,
    autoStart: false,
    linkedTaskId: null,
  };

  // `endsAt` (epoch ms) is set while running; `remainingMs` is the frozen
  // countdown while paused/stopped. `cycleCount` counts completed focus
  // sessions since the last long break (used for the cycle dots and to
  // decide focus -> short vs. long break).
  const DEFAULT_STATE = {
    phase: 'focus',
    running: false,
    endsAt: null,
    remainingMs: null,
    cycleCount: 0,
    // Monotonically increasing write counter (+ wall-clock of the write).
    // Two open tabs share this state through localStorage; whichever holds
    // the higher `seq` is the newer truth (see adoptIfNewer).
    seq: 0,
    updatedAt: 0,
  };

  function loadJson(key, fallback) {
    try {
      const parsed = JSON.parse(localStorage.getItem(key));
      return parsed && typeof parsed === 'object' ? { ...fallback, ...parsed } : { ...fallback };
    } catch {
      return { ...fallback };
    }
  }

  let settings = loadJson(SETTINGS_KEY, DEFAULT_SETTINGS);
  let state = loadJson(STATE_KEY, DEFAULT_STATE);

  function saveSettings() {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
  }
  function saveState() {
    state.seq = (state.seq || 0) + 1;
    state.updatedAt = Date.now();
    localStorage.setItem(STATE_KEY, JSON.stringify(state));
  }

  /**
   * Adopts the state another tab persisted if it's newer than ours (higher
   * `seq`, or same `seq` and later `updatedAt`). Returns true if it did.
   * Used both by the `storage` event and - crucially - right before a tab
   * advances a phase at `endsAt`, so two tabs never both advance (which
   * would double-count cycles and chime twice).
   */
  function adoptIfNewer() {
    const remote = loadJson(STATE_KEY, DEFAULT_STATE);
    const newer = remote.seq > state.seq || (remote.seq === state.seq && remote.updatedAt > state.updatedAt);
    if (!newer) {
      return false;
    }
    state = remote;
    render();
    return true;
  }

  window.addEventListener('storage', (event) => {
    if (event.key === STATE_KEY) {
      adoptIfNewer();
    } else if (event.key === SETTINGS_KEY) {
      settings = loadJson(SETTINGS_KEY, DEFAULT_SETTINGS);
      render();
    }
  });

  function phaseDurationMs(phase) {
    const minutes = phase === 'focus' ? settings.workMin : phase === 'shortBreak' ? settings.shortBreakMin : settings.longBreakMin;
    return Math.max(1, minutes) * 60 * 1000;
  }

  function remainingMs() {
    if (state.running && state.endsAt) {
      return Math.max(0, state.endsAt - Date.now());
    }
    return state.remainingMs != null ? state.remainingMs : phaseDurationMs(state.phase);
  }

  function formatTime(ms) {
    const totalSeconds = Math.max(0, Math.round(ms / 1000));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  }

  // --- Web Audio chime (no vendored sound asset - a couple of oscillator
  // beeps synthesized on the fly) + optional browser Notification ---------

  let audioCtx = null;

  function playChime() {
    if (!settings.soundOn) {
      return;
    }
    try {
      audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
      const now = audioCtx.currentTime;
      [0, 0.18].forEach((offset, i) => {
        const osc = audioCtx.createOscillator();
        const gain = audioCtx.createGain();
        osc.frequency.value = i === 0 ? 880 : 1108.73;
        gain.gain.setValueAtTime(0.0001, now + offset);
        gain.gain.exponentialRampToValueAtTime(0.2, now + offset + 0.01);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + offset + 0.16);
        osc.connect(gain).connect(audioCtx.destination);
        osc.start(now + offset);
        osc.stop(now + offset + 0.2);
      });
    } catch (err) {
      console.error('Pomodoro chime failed', err);
    }
  }

  function notify(title, body) {
    if (!settings.notifyOn || !('Notification' in window) || Notification.permission !== 'granted') {
      return;
    }
    try {
      new Notification(title, { body });
    } catch (err) {
      console.error('Pomodoro notification failed', err);
    }
  }

  function requestNotificationPermission() {
    if ('Notification' in window && Notification.permission === 'default') {
      Notification.requestPermission();
    }
  }

  // --- phase transitions ---------------------------------------------------

  function nextPhase(currentPhase, cycleCount) {
    if (currentPhase === 'focus') {
      const completed = cycleCount + 1;
      return completed % settings.cyclesBeforeLong === 0 ? 'longBreak' : 'shortBreak';
    }
    return 'focus';
  }

  function advancePhase() {
    const finishedFocus = state.phase === 'focus';
    state.cycleCount = finishedFocus ? state.cycleCount + 1 : state.cycleCount;
    const upcoming = nextPhase(state.phase, finishedFocus ? state.cycleCount - 1 : state.cycleCount);
    state.phase = upcoming;
    if (upcoming === 'focus' && state.cycleCount >= settings.cyclesBeforeLong) {
      state.cycleCount = 0;
    }
    playChime();
    notify('dotNotes Pomodoro', `${PHASE_LABELS[upcoming]} started`);
    if (settings.autoStart) {
      state.running = true;
      state.endsAt = Date.now() + phaseDurationMs(upcoming);
      state.remainingMs = null;
    } else {
      state.running = false;
      state.endsAt = null;
      state.remainingMs = phaseDurationMs(upcoming);
    }
    saveState();
    render();
  }

  function tick() {
    if (state.running && state.endsAt && Date.now() >= state.endsAt) {
      // Another tab may already have advanced this phase - if so, take its
      // state instead of advancing (and chiming) a second time.
      if (adoptIfNewer()) {
        return;
      }
      advancePhase();
      return;
    }
    render();
  }

  // --- controls -------------------------------------------------------------

  function start() {
    if (state.running) {
      return;
    }
    const remaining = remainingMs();
    state.running = true;
    state.endsAt = Date.now() + remaining;
    state.remainingMs = null;
    saveState();
    render();
  }

  function pause() {
    if (!state.running) {
      return;
    }
    state.remainingMs = remainingMs();
    state.running = false;
    state.endsAt = null;
    saveState();
    render();
  }

  function reset() {
    state = { ...DEFAULT_STATE, phase: 'focus', remainingMs: phaseDurationMs('focus'), seq: state.seq || 0 };
    saveState();
    render();
  }

  function skip() {
    adoptIfNewer(); // act on the freshest state if another tab moved on
    advancePhase();
  }

  function updateSettings(patch) {
    const durationBefore = phaseDurationMs(state.phase);
    settings = { ...settings, ...patch };
    saveSettings();
    // Only touch the countdown when the CURRENT phase's own duration changed
    // and the timer is idle: not running, and not paused part-way through
    // (a frozen countdown that differs from the full old duration). Any
    // other setting (sound, notifications, auto-start, linked task, cycles)
    // never affects the countdown.
    const durationAfter = phaseDurationMs(state.phase);
    const idle = !state.running && (state.remainingMs == null || state.remainingMs === durationBefore);
    if (idle && durationAfter !== durationBefore) {
      state.remainingMs = durationAfter;
      saveState();
    }
    render();
  }

  function setLinkedTask(taskId) {
    updateSettings({ linkedTaskId: taskId || null });
  }

  // --- rendering -------------------------------------------------------------

  let navIndicatorEl = null;
  let widgetContainerEl = null;
  let widgetTaskOptionsProvider = null; // () => [{ id, title }]
  let tickTimer = null;

  function isBoardVisible() {
    const boardView = document.getElementById('tasks-board-view');
    return !!boardView && !boardView.classList.contains('hidden');
  }

  function renderNavIndicator() {
    if (!navIndicatorEl) {
      return;
    }
    const show = state.running && !isBoardVisible();
    navIndicatorEl.classList.toggle('hidden', !show);
    if (show) {
      navIndicatorEl.textContent = `${PHASE_LABELS[state.phase]} · ${formatTime(remainingMs())}`;
    }
  }

  function renderDocumentTitle() {
    const base = 'dotNotes';
    document.title = state.running ? `${formatTime(remainingMs())} · ${base}` : base;
  }

  function renderCycleDots(container) {
    container.innerHTML = '';
    for (let i = 0; i < settings.cyclesBeforeLong; i++) {
      const dot = document.createElement('span');
      dot.className = 'pomodoro-cycle-dot';
      if (i < state.cycleCount) {
        dot.classList.add('pomodoro-cycle-dot-filled');
      }
      container.appendChild(dot);
    }
  }

  function renderWidget() {
    if (!widgetContainerEl || widgetContainerEl.classList.contains('hidden')) {
      return;
    }
    const timeEl = widgetContainerEl.querySelector('#pomodoro-time');
    const phaseEl = widgetContainerEl.querySelector('#pomodoro-phase');
    const startPauseBtn = widgetContainerEl.querySelector('#pomodoro-start-pause-btn');
    const dotsEl = widgetContainerEl.querySelector('#pomodoro-cycle-dots');
    if (!timeEl) {
      return; // widget DOM not built yet
    }
    timeEl.textContent = formatTime(remainingMs());
    phaseEl.textContent = PHASE_LABELS[state.phase];
    startPauseBtn.textContent = state.running ? 'Pause' : 'Start';
    renderCycleDots(dotsEl);
  }

  function render() {
    renderNavIndicator();
    renderDocumentTitle();
    renderWidget();
  }

  function buildSettingsPopover() {
    const pop = document.createElement('div');
    pop.className = 'pomodoro-settings-popover hidden';
    pop.innerHTML = `
      <label class="pomodoro-settings-row"><span>Focus (min)</span><input type="number" min="1" id="pomodoro-set-work" /></label>
      <label class="pomodoro-settings-row"><span>Short break (min)</span><input type="number" min="1" id="pomodoro-set-short" /></label>
      <label class="pomodoro-settings-row"><span>Long break (min)</span><input type="number" min="1" id="pomodoro-set-long" /></label>
      <label class="pomodoro-settings-row"><span>Cycles before long break</span><input type="number" min="1" id="pomodoro-set-cycles" /></label>
      <label class="pomodoro-settings-row pomodoro-settings-row-checkbox"><input type="checkbox" id="pomodoro-set-sound" /><span>Sound</span></label>
      <label class="pomodoro-settings-row pomodoro-settings-row-checkbox"><input type="checkbox" id="pomodoro-set-notify" /><span>Browser notifications</span></label>
      <label class="pomodoro-settings-row pomodoro-settings-row-checkbox"><input type="checkbox" id="pomodoro-set-autostart" /><span>Auto-start next phase</span></label>
      <label class="pomodoro-settings-row"><span>Linked task</span><select id="pomodoro-set-task"><option value="">None</option></select></label>
    `;
    return pop;
  }

  function fillSettingsPopover(pop) {
    pop.querySelector('#pomodoro-set-work').value = settings.workMin;
    pop.querySelector('#pomodoro-set-short').value = settings.shortBreakMin;
    pop.querySelector('#pomodoro-set-long').value = settings.longBreakMin;
    pop.querySelector('#pomodoro-set-cycles').value = settings.cyclesBeforeLong;
    pop.querySelector('#pomodoro-set-sound').checked = settings.soundOn;
    pop.querySelector('#pomodoro-set-notify').checked = settings.notifyOn;
    pop.querySelector('#pomodoro-set-autostart').checked = settings.autoStart;

    const taskSelect = pop.querySelector('#pomodoro-set-task');
    const options = widgetTaskOptionsProvider ? widgetTaskOptionsProvider() : [];
    taskSelect.innerHTML = '<option value="">None</option>';
    for (const task of options) {
      const opt = document.createElement('option');
      opt.value = task.id;
      opt.textContent = `${task.id} - ${task.title}`;
      taskSelect.appendChild(opt);
    }
    taskSelect.value = settings.linkedTaskId || '';
  }

  function wireSettingsPopover(pop) {
    pop.querySelector('#pomodoro-set-work').addEventListener('change', (e) => updateSettings({ workMin: Number(e.target.value) || DEFAULT_SETTINGS.workMin }));
    pop.querySelector('#pomodoro-set-short').addEventListener('change', (e) => updateSettings({ shortBreakMin: Number(e.target.value) || DEFAULT_SETTINGS.shortBreakMin }));
    pop.querySelector('#pomodoro-set-long').addEventListener('change', (e) => updateSettings({ longBreakMin: Number(e.target.value) || DEFAULT_SETTINGS.longBreakMin }));
    pop.querySelector('#pomodoro-set-cycles').addEventListener('change', (e) => updateSettings({ cyclesBeforeLong: Number(e.target.value) || DEFAULT_SETTINGS.cyclesBeforeLong }));
    pop.querySelector('#pomodoro-set-sound').addEventListener('change', (e) => updateSettings({ soundOn: e.target.checked }));
    pop.querySelector('#pomodoro-set-notify').addEventListener('change', (e) => {
      updateSettings({ notifyOn: e.target.checked });
      if (e.target.checked) {
        requestNotificationPermission();
      }
    });
    pop.querySelector('#pomodoro-set-autostart').addEventListener('change', (e) => updateSettings({ autoStart: e.target.checked }));
    pop.querySelector('#pomodoro-set-task').addEventListener('change', (e) => setLinkedTask(e.target.value || null));
  }

  /**
   * Builds (once per container) the full widget - big countdown, phase
   * label, cycle dots, start/pause/reset/skip, and a settings popover -
   * into `containerEl` (index.html's `#pomodoro-widget`, inside the Kanban
   * board header). `getTaskOptions` is a `() => [{id, title}]` callback so
   * the linked-task dropdown can list current tasks without this module
   * depending on js/tasks.js directly.
   */
  function mount(containerEl, getTaskOptions) {
    widgetContainerEl = containerEl;
    widgetTaskOptionsProvider = getTaskOptions || null;
    containerEl.classList.remove('hidden');

    if (containerEl.dataset.pomodoroBuilt === 'true') {
      fillSettingsPopover(containerEl.querySelector('.pomodoro-settings-popover'));
      render();
      return;
    }
    containerEl.dataset.pomodoroBuilt = 'true';
    containerEl.innerHTML = `
      <div class="pomodoro-widget-main">
        <span id="pomodoro-phase" class="pomodoro-phase-label"></span>
        <span id="pomodoro-time" class="pomodoro-time"></span>
        <div id="pomodoro-cycle-dots" class="pomodoro-cycle-dots"></div>
        <div class="pomodoro-widget-controls">
          <button type="button" id="pomodoro-start-pause-btn" class="mini-modal-btn">Start</button>
          <button type="button" id="pomodoro-reset-btn" class="mini-modal-btn" title="Reset">↺</button>
          <button type="button" id="pomodoro-skip-btn" class="mini-modal-btn" title="Skip to next phase" aria-label="Skip to next phase">Skip</button>
          <button type="button" id="pomodoro-settings-btn" class="icon-btn" title="Pomodoro settings">⚙</button>
        </div>
      </div>
    `;
    const settingsPopover = buildSettingsPopover();
    containerEl.appendChild(settingsPopover);
    fillSettingsPopover(settingsPopover);
    wireSettingsPopover(settingsPopover);

    containerEl.querySelector('#pomodoro-start-pause-btn').addEventListener('click', () => (state.running ? pause() : start()));
    containerEl.querySelector('#pomodoro-reset-btn').addEventListener('click', reset);
    containerEl.querySelector('#pomodoro-skip-btn').addEventListener('click', skip);
    containerEl.querySelector('#pomodoro-settings-btn').addEventListener('click', () => {
      fillSettingsPopover(settingsPopover);
      settingsPopover.classList.toggle('hidden');
    });
    document.addEventListener('mousedown', (event) => {
      if (!settingsPopover.classList.contains('hidden') && !containerEl.contains(event.target)) {
        settingsPopover.classList.add('hidden');
      }
    });

    render();
  }

  function unmount() {
    widgetContainerEl = null;
    widgetTaskOptionsProvider = null;
  }

  /** Wires the compact top-nav indicator (index.html's `#pomodoro-nav-indicator`) - shown whenever the timer is running and the board isn't visible. */
  function init(navIndicator, onNavClick) {
    navIndicatorEl = navIndicator;
    if (navIndicatorEl && onNavClick) {
      navIndicatorEl.addEventListener('click', onNavClick);
    }
    // Normalize the frozen countdown if settings changed since the last
    // session (e.g. a shorter work length than however long is left).
    if (!state.running && state.remainingMs == null) {
      state.remainingMs = phaseDurationMs(state.phase);
    }
    clearInterval(tickTimer);
    tickTimer = setInterval(tick, 1000);
    render();
  }

  return {
    init,
    mount,
    unmount,
    start,
    pause,
    reset,
    skip,
    updateSettings,
    setLinkedTask,
    getSettings: () => ({ ...settings }),
    getState: () => ({ ...state, remainingMs: remainingMs() }),
    formatTime,
    render,
  };
})();

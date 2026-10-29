import { DAY_LENGTH, BIOME } from './config.js';
import { player, setWorldTime, scene } from './core.js';
import { getBiome } from './worldgen.js';
import { setWorldTimeGlobal } from './daynight.js';

// ============================================================
// BIOME NAMES
// ============================================================
const BIOME_NAMES = {
  [BIOME.PLAINS]: 'plains',
  [BIOME.FOREST]: 'forest',
  [BIOME.MOUNTAIN]: 'mountain',
  [BIOME.DESERT]: 'desert',
  [BIOME.SNOWY]: 'snowy',
  [BIOME.OLD_GROWTH]: 'old_growth'
};

const BIOME_NAME_TO_ID = {
  'plains': BIOME.PLAINS,
  'forest': BIOME.FOREST,
  'mountain': BIOME.MOUNTAIN,
  'desert': BIOME.DESERT,
  'snowy': BIOME.SNOWY,
  'snow': BIOME.SNOWY,
  'old_growth': BIOME.OLD_GROWTH,
  'oldgrowth': BIOME.OLD_GROWTH,
  'ogg': BIOME.OLD_GROWTH
};

// ============================================================
// TIME VALUES
// ============================================================
// t = worldTime / DAY_LENGTH
// t=0    → midnight
// t=0.25 → sunrise
// t=0.5  → noon
// t=0.75 → sunset

const TIME_VALUES = {
  'dawn':     DAY_LENGTH * 0.25,
  'sunrise':  DAY_LENGTH * 0.25,
  'morning':  DAY_LENGTH * 0.3,
  'noon':     DAY_LENGTH * 0.5,
  'day':      DAY_LENGTH * 0.5,
  'afternoon': DAY_LENGTH * 0.65,
  'dusk':     DAY_LENGTH * 0.75,
  'sunset':   DAY_LENGTH * 0.75,
  'evening':  DAY_LENGTH * 0.8,
  'night':    DAY_LENGTH * 0.85,
  'midnight': 0
};

// ============================================================
// COMMAND DEFINITIONS
// ============================================================
const COMMANDS = {
  'time': {
    description: 'Set the world time',
    usage: '/time set <dawn|noon|dusk|night|midnight>',
    subcommands: {
      'set': {
        description: 'Set time to a specific value',
        usage: '/time set <dawn|noon|dusk|night|midnight>',
        suggestions: Object.keys(TIME_VALUES),
        run(args) {
          const value = args[0]?.toLowerCase();
          if (!value) return { error: 'Missing time value. Try: dawn, noon, dusk, night, midnight' };
          if (!(value in TIME_VALUES)) return { error: `Unknown time: "${value}"` };
          setWorldTimeGlobal(TIME_VALUES[value]);
          return { message: `Time set to ${value}` };
        }
      }
    },
    run() {
      return { error: 'Usage: /time set <value>' };
    }
  },

  'locate': {
    description: 'Find nearest biome',
    usage: '/locate biome <biome_name>',
    subcommands: {
      'biome': {
        description: 'Locate the nearest biome',
        usage: '/locate biome <biome_name>',
        suggestions: Object.keys(BIOME_NAME_TO_ID),
        run(args) {
          const targetName = args[0]?.toLowerCase();
          if (!targetName) return { error: 'Missing biome name. Try: plains, forest, mountain, desert, snowy, old_growth' };
          const targetId = BIOME_NAME_TO_ID[targetName];
          if (targetId === undefined) return { error: `Unknown biome: "${targetName}"` };

          const found = locateBiome(targetId, player.pos.x, player.pos.z);
          if (!found) return { error: `No ${targetName} biome found nearby` };

          const dx = Math.round(found.x - player.pos.x);
          const dz = Math.round(found.z - player.pos.z);
          const dist = Math.round(Math.hypot(dx, dz));
          return {
            message: `Nearest ${targetName} at ${found.x}, ${found.z} (${dist} blocks away)`,
            action: () => {
              // Optionally, could show a marker or particle
            }
          };
        }
      }
    },
    run() {
      return { error: 'Usage: /locate biome <name>' };
    }
  },

  'tp': {
    description: 'Teleport the player',
    usage: '/tp <x> <y> <z>',
    run(args) {
      if (args.length < 3) return { error: 'Usage: /tp <x> <y> <z>' };
      const x = parseFloat(args[0]);
      const y = parseFloat(args[1]);
      const z = parseFloat(args[2]);
      if (isNaN(x) || isNaN(y) || isNaN(z)) return { error: 'Coordinates must be numbers' };
      if (y < -100 || y > 500) return { error: 'Y must be between -100 and 500' };

      player.pos.set(x, y, z);
      player.vel.set(0, 0, 0);
      player.fallStartY = null;
      return { message: `Teleported to ${x}, ${y}, ${z}` };
    }
  },

  'help': {
    description: 'Show available commands',
    usage: '/help',
    run() {
      const lines = Object.entries(COMMANDS).map(([name, cmd]) => {
        return `/${name} — ${cmd.description}`;
      });
      return { message: lines.join('\n') };
    }
  }
};

// ============================================================
// BIOME LOCATOR
// ============================================================
function locateBiome(targetId, px, pz) {
  // Spiral outward from player
  const STEP = 8;
  const MAX_RADIUS = 500;

  for (let radius = STEP; radius <= MAX_RADIUS; radius += STEP) {
    const points = Math.max(8, Math.floor(radius / 2));
    for (let i = 0; i < points; i++) {
      const angle = (i / points) * Math.PI * 2;
      const x = Math.floor(px + Math.cos(angle) * radius);
      const z = Math.floor(pz + Math.sin(angle) * radius);
      if (getBiome(x, z) === targetId) {
        return { x, z };
      }
    }
  }
  return null;
}

// ============================================================
// COMMAND PARSER
// ============================================================
function parseCommand(input) {
  const trimmed = input.trim();
  if (!trimmed) return null;

  // Remove leading /
  const noSlash = trimmed.startsWith('/') ? trimmed.slice(1) : trimmed;
  if (!noSlash) return null;

  // Split by whitespace
  const parts = noSlash.split(/\s+/);
  const name = parts[0].toLowerCase();
  const args = parts.slice(1);

  return { name, args };
}

// ============================================================
// COMMAND EXECUTOR
// ============================================================
export function executeCommand(input) {
  const parsed = parseCommand(input);
  if (!parsed) return { error: 'Empty command' };

  const cmd = COMMANDS[parsed.name];
  if (!cmd) return { error: `Unknown command: /${parsed.name}. Try /help` };

  // Check subcommands first
  if (cmd.subcommands && parsed.args.length > 0) {
    const subName = parsed.args[0].toLowerCase();
    const sub = cmd.subcommands[subName];
    if (sub) {
      return sub.run(parsed.args.slice(1));
    }
  }

  // Otherwise run the main command
  return cmd.run(parsed.args);
}

// ============================================================
// AUTOCOMPLETE
// ============================================================
export function getCompletions(input) {
  const noSlash = input.startsWith('/') ? input.slice(1) : input;
  const parts = noSlash.split(/\s+/);
  const currentPart = parts[parts.length - 1];
  const isPartial = !input.endsWith(' ') && currentPart.length > 0;

  // If typing the command name
  if (parts.length === 1) {
    const names = Object.keys(COMMANDS);
    return names
      .filter(n => n.startsWith(currentPart.toLowerCase()))
      .map(n => '/' + n);
  }

  // If typing subcommand or argument
  const cmdName = parts[0].toLowerCase();
  const cmd = COMMANDS[cmdName];
  if (!cmd) return [];

  // Subcommand suggestions
  if (cmd.subcommands && parts.length === 2) {
    const subs = Object.keys(cmd.subcommands);
    return subs
      .filter(s => s.startsWith(currentPart.toLowerCase()))
      .map(s => `/${cmdName} ${s}`);
  }

  // Argument suggestions
  if (cmd.subcommands && parts.length === 3) {
    const subName = parts[1].toLowerCase();
    const sub = cmd.subcommands[subName];
    if (sub && sub.suggestions) {
      return sub.suggestions
        .filter(s => s.startsWith(currentPart.toLowerCase()))
        .map(s => `/${cmdName} ${subName} ${s}`);
    }
  }

  return [];
}

// ============================================================
// CHAT UI
// ============================================================
let chatOpen = false;
let chatLog = [];
const MAX_LOG = 20;

let logEl, inputEl, wrapperEl;

export function setupCommands() {
  wrapperEl = document.getElementById('chat-wrapper');
  logEl = document.getElementById('chat-log');
  inputEl = document.getElementById('chat-input');
  const acEl = document.getElementById('chat-autocomplete');

  if (!wrapperEl) {
    console.error('Chat wrapper not found in HTML');
    return;
  }

  // ============================================================
  // OPEN CHAT WITH /
  // ============================================================
  document.addEventListener('keydown', (e) => {
    // If typing in the chat input, let it handle its own keys
    if (document.activeElement === inputEl) return;

    // Don't trigger if in another input
    const tag = document.activeElement.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA') return;

    // Press / to open chat with /
    if (e.key === '/') {
      e.preventDefault();
      openChat('/');
    }
    // Press T to open chat without /
    if (e.key === 't' || e.key === 'T') {
      if (e.ctrlKey || e.metaKey) return;
      e.preventDefault();
      openChat('');
    }
  });

  // ============================================================
  // INPUT HANDLING
  // ============================================================
  inputEl.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      const val = inputEl.value;
      if (val.trim()) {
        appendMessage('> ' + val, 'chat-input-echo');
        const result = executeCommand(val);
        if (result.error) {
          appendMessage(result.error, 'chat-error');
        } else if (result.message) {
          // Support multi-line messages
          const lines = String(result.message).split('\n');
          for (const line of lines) {
            appendMessage(line, 'chat-info');
          }
          if (result.action) result.action();
        }
      }
      closeChat();
    }

    if (e.key === 'Escape') {
      e.preventDefault();
      closeChat();
    }

    // Tab autocomplete
    if (e.key === 'Tab') {
      e.preventDefault();
      const completions = getCompletions(inputEl.value);
      if (completions.length === 1) {
        inputEl.value = completions[0] + ' ';
      } else if (completions.length > 1) {
        appendMessage('Suggestions: ' + completions.map(c => c.split(' ').pop()).join(', '), 'chat-info');
      }
    }

    // Live autocomplete suggestions
    setTimeout(() => {
      updateAutocomplete(acEl);
    }, 0);
  });

  inputEl.addEventListener('input', () => {
    updateAutocomplete(acEl);
  });

  // ============================================================
  // CLICK OUTSIDE TO CLOSE
  // ============================================================
  document.addEventListener('mousedown', (e) => {
    if (chatOpen && !wrapperEl.contains(e.target)) {
      closeChat();
    }
  });
}

function openChat(initialValue) {
  if (!wrapperEl) return;
  chatOpen = true;
  wrapperEl.classList.add('open');
  inputEl.value = initialValue || '';
  inputEl.focus();
  // Move cursor to end
  inputEl.setSelectionRange(inputEl.value.length, inputEl.value.length);
}

function closeChat() {
  if (!wrapperEl) return;
  chatOpen = false;
  wrapperEl.classList.remove('open');
  inputEl.value = '';
  inputEl.blur();
  // Re-lock pointer
  if (!document.pointerLockElement) {
    const canvas = document.querySelector('canvas');
    if (canvas) canvas.requestPointerLock?.();
  }
}

// ============================================================
// MESSAGE LOG
// ============================================================
function appendMessage(text, className) {
  chatLog.push({ text, className, time: performance.now() });
  if (chatLog.length > MAX_LOG) chatLog.shift();
  renderLog();
}

function renderLog() {
  if (!logEl) return;
  logEl.innerHTML = '';
  for (const msg of chatLog) {
    const div = document.createElement('div');
    div.className = 'chat-line ' + (msg.className || '');
    div.textContent = msg.text;
    logEl.appendChild(div);
  }
  logEl.scrollTop = logEl.scrollHeight;
}

// ============================================================
// AUTOCOMPLETE DISPLAY
// ============================================================
function updateAutocomplete(acEl) {
  if (!acEl) return;
  const completions = getCompletions(inputEl.value);
  if (completions.length === 0 || !chatOpen) {
    acEl.style.display = 'none';
    acEl.innerHTML = '';
    return;
  }
  acEl.style.display = 'block';
  acEl.innerHTML = '';
  const shown = completions.slice(0, 8);
  for (const c of shown) {
    const div = document.createElement('div');
    div.className = 'chat-ac-item';
    div.textContent = c;
    div.addEventListener('mousedown', (e) => {
      e.preventDefault();
      inputEl.value = c + ' ';
      inputEl.focus();
      updateAutocomplete(acEl);
    });
    acEl.appendChild(div);
  }
}

// ============================================================
// PUBLIC API
// ============================================================
export function isChatOpen() { return chatOpen; }

export function addChatMessage(text, type) {
  appendMessage(text, type || 'chat-info');
}
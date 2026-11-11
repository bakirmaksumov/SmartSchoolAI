/*
 * Waiting games — a short game to pass the time while the server checks an answer or reads a
 * photo, so a slow AI call feels like a moment of play instead of a frozen spinner. The check
 * runs on its own regardless of what the student taps here.
 *
 * Three games, each of which makes the first move itself so the screen never looks unstarted:
 *   tictactoe  the computer places the first O immediately; the student answers with X
 *   rps        rock-paper-scissors: the computer has already chosen (hidden) before the student does
 *   sequence   the computer flashes a short sequence of pads; the student repeats it
 * A new wait picks one at random, never the same game twice in a row on one page. "New game"
 * restarts the game being played.
 *
 * Two ways to host the games:
 *
 *   WaitingGame.start() / .finish(reveal) / .close()
 *       Drives a built-in modal, used where the wait has no UI of its own (the typed
 *       "Check answer" / "Check my work" buttons). start() opens it with a fresh game; finish()
 *       keeps the game playable but offers a "See result" button that runs `reveal` and closes;
 *       close() dismisses without a result.
 *
 *   WaitingGame.attach(boardEl, statusEl) → { newGame(), restart(), stop() }
 *       Mounts the games into a caller's own elements, used where the wait already has a place to
 *       live (the photo-solution loading stage sits inside its own modal). The caller calls
 *       newGame() when a wait begins and stop() when it ends.
 *
 * Every delayed move goes through one timer registry per host: stop()/restart() cancel them all, so
 * a move scheduled in one round can never land in the next.
 */
(function () {
  'use strict';

  var GAMES = ['tictactoe', 'rps', 'sequence'];
  var lastGame = null;   // never repeat the previous game on this page
  var forcedNext = null; // set by tests (and only by them) through WaitingGame.setNextGame()

  function pickGame() {
    var choice;
    if (forcedNext) {
      choice = forcedNext;
      forcedNext = null;
    } else {
      var options = GAMES.filter(function (g) { return g !== lastGame; });
      choice = options[Math.floor(Math.random() * options.length)];
    }
    lastGame = choice;
    return choice;
  }

  function randomInt(n) { return Math.floor(Math.random() * n); }

  // ── Tic-tac-toe ────────────────────────────────────────────────────────────
  var PLAYER = 'X';
  var CPU    = 'O';
  var LINES = [
    [0, 1, 2], [3, 4, 5], [6, 7, 8],   // rows
    [0, 3, 6], [1, 4, 7], [2, 5, 8],   // columns
    [0, 4, 8], [2, 4, 6]               // diagonals
  ];

  function winningLine(b) {
    for (var l = 0; l < LINES.length; l++) {
      var line = LINES[l];
      if (b[line[0]] && b[line[0]] === b[line[1]] && b[line[1]] === b[line[2]]) return line;
    }
    return null;
  }

  function ticTacToe(ctx) {
    var boardEl = ctx.board;
    var board = ['', '', '', '', '', '', '', '', ''];
    var locked = false;   // true while it is the computer's turn or the game is over

    for (var i = 0; i < 9; i++) {
      var cell = document.createElement('button');
      cell.type = 'button';
      cell.className = 'wg-cell';
      cell.dataset.i = i;
      cell.setAttribute('aria-label', 'Square ' + (i + 1));
      cell.addEventListener('click', onCellClick);
      boardEl.appendChild(cell);
    }

    // The computer opens at once, on the board, before the student has touched anything.
    var openings = [0, 2, 4, 6, 8];
    place(openings[randomInt(openings.length)], CPU);
    ctx.status('Computer played O — your turn, place an X');

    function onCellClick(e) {
      var i = parseInt(e.currentTarget.dataset.i, 10);
      if (locked || board[i]) return;

      place(i, PLAYER);
      if (endIfOver()) return;

      locked = true;
      ctx.status('Computer is thinking…');
      ctx.later(cpuMove, 380);   // a short beat so moves feel taken
    }

    function cpuMove() {
      var i = chooseCpuMove();
      if (i !== -1) place(i, CPU);
      if (endIfOver()) return;

      locked = false;
      ctx.status('Your turn');
    }

    function endIfOver() {
      var win = winningLine(board);
      if (win) {
        win.forEach(function (i) { cellAt(i).classList.add('wg-win'); });
        locked = true;
        ctx.status(board[win[0]] === PLAYER ? 'You win! 🎉' : 'Computer wins — try again', true);
        return true;
      }
      if (board.every(Boolean)) {
        locked = true;
        ctx.status("It's a draw", true);
        return true;
      }
      return false;
    }

    function place(i, mark) {
      board[i] = mark;
      var c = cellAt(i);
      c.textContent = mark;
      c.classList.add(mark === PLAYER ? 'wg-x' : 'wg-o');
      c.disabled = true;
    }

    function cellAt(i) { return boardEl.querySelector('.wg-cell[data-i="' + i + '"]'); }

    // Deliberately beatable: take a win, block a loss, then prefer centre and corners.
    function chooseCpuMove() {
      var win = findLine(CPU);
      if (win !== -1) return win;
      var block = findLine(PLAYER);
      if (block !== -1) return block;
      if (!board[4]) return 4;

      var corners = [0, 2, 6, 8].filter(function (i) { return !board[i]; });
      if (corners.length) return corners[randomInt(corners.length)];

      var free = [];
      for (var k = 0; k < 9; k++) if (!board[k]) free.push(k);
      return free.length ? free[randomInt(free.length)] : -1;
    }

    function findLine(mark) {
      for (var l = 0; l < LINES.length; l++) {
        var line = LINES[l], marks = 0, empty = -1;
        for (var k = 0; k < 3; k++) {
          if (board[line[k]] === mark) marks++;
          else if (!board[line[k]]) empty = line[k];
        }
        if (marks === 2 && empty !== -1) return empty;
      }
      return -1;
    }
  }

  // ── Rock, paper, scissors ──────────────────────────────────────────────────
  var MOVES = {
    rock:     { label: 'Rock',     icon: '✊', beats: 'scissors' },
    paper:    { label: 'Paper',    icon: '✋', beats: 'rock' },
    scissors: { label: 'Scissors', icon: '✌️', beats: 'paper' }
  };

  function rockPaperScissors(ctx) {
    var names = Object.keys(MOVES);
    var computer = names[randomInt(names.length)];   // chosen now, kept out of the DOM until the student answers

    var wrap = document.createElement('div');
    wrap.className = 'wg-rps';
    wrap.innerHTML =
      '<div class="wg-rps-cpu"><span class="wg-rps-label">Computer</span>' +
      '<div class="wg-rps-pick" aria-live="polite">?</div></div>' +
      '<div class="wg-rps-choices"></div>';
    var pickEl = wrap.querySelector('.wg-rps-pick');
    var choices = wrap.querySelector('.wg-rps-choices');

    names.forEach(function (name) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'wg-rps-btn';
      b.dataset.move = name;
      b.innerHTML = '<span class="wg-rps-icon"></span><span class="wg-rps-name"></span>';
      b.querySelector('.wg-rps-icon').textContent = MOVES[name].icon;
      b.querySelector('.wg-rps-name').textContent = MOVES[name].label;
      b.addEventListener('click', function () { answer(name); });
      choices.appendChild(b);
    });
    ctx.board.appendChild(wrap);
    ctx.status('Computer has chosen — pick rock, paper or scissors');

    function answer(move) {
      pickEl.textContent = MOVES[computer].icon + ' ' + MOVES[computer].label;
      pickEl.classList.add('wg-revealed');
      choices.querySelectorAll('button').forEach(function (b) {
        b.disabled = true;
        if (b.dataset.move === move) b.classList.add('wg-chosen');
      });

      if (move === computer) ctx.status("It's a tie — press New game to play again", true);
      else if (MOVES[move].beats === computer) ctx.status('You win! 🎉', true);
      else ctx.status('Computer wins — press New game to try again', true);
    }
  }

  // ── Repeat the sequence ────────────────────────────────────────────────────
  var PAD_COLORS = ['red', 'blue', 'green', 'yellow'];
  var SEQUENCE_LENGTH = 4;
  var FLASH_GAP_MS = 700;   // between the start of one flash and the next
  var FLASH_MS = 450;

  function repeatSequence(ctx) {
    var sequence = [];
    for (var s = 0; s < SEQUENCE_LENGTH; s++) sequence.push(randomInt(PAD_COLORS.length));
    ctx.board.dataset.sequence = sequence.join(',');   // read by the end-to-end tests

    var pads = PAD_COLORS.map(function (color, index) {
      var pad = document.createElement('button');
      pad.type = 'button';
      pad.className = 'wg-pad wg-pad-' + color;
      pad.dataset.pad = index;
      pad.disabled = true;
      pad.setAttribute('aria-label', color + ' pad');
      pad.addEventListener('click', function () { onPad(index); });
      ctx.board.appendChild(pad);
      return pad;
    });

    var position = 0;   // how much of the sequence the student has repeated
    var listening = false;

    ctx.status('Watch the sequence…');
    sequence.forEach(function (padIndex, k) {
      ctx.later(function () { flash(padIndex); }, FLASH_GAP_MS * (k + 1));
    });
    ctx.later(function () {
      listening = true;
      pads.forEach(function (p) { p.disabled = false; });
      ctx.status('Your turn — repeat it (0/' + SEQUENCE_LENGTH + ')');
    }, FLASH_GAP_MS * SEQUENCE_LENGTH + FLASH_MS + 150);

    function flash(padIndex) {
      pads[padIndex].classList.add('wg-lit');
      ctx.later(function () { pads[padIndex].classList.remove('wg-lit'); }, FLASH_MS);
    }

    function onPad(index) {
      if (!listening) return;
      flash(index);

      if (index !== sequence[position]) {
        listening = false;
        pads.forEach(function (p) { p.disabled = true; });
        ctx.status('Not quite — press New game to try again', true);
        return;
      }

      position++;
      if (position === sequence.length) {
        listening = false;
        pads.forEach(function (p) { p.disabled = true; });
        ctx.status('You remembered it all! 🎉', true);
      } else {
        ctx.status('Your turn — repeat it (' + position + '/' + SEQUENCE_LENGTH + ')');
      }
    }
  }

  var IMPLEMENTATIONS = { tictactoe: ticTacToe, rps: rockPaperScissors, sequence: repeatSequence };

  // ── One host: a board element, a status line and a timer registry ──────────
  function createController(boardEl, statusEl) {
    var timers = [];
    var round = 0;        // bumped by every stop(); a callback from an older round is ignored
    var current = null;

    function setStatus(text, isFinal) {
      if (!statusEl) return;
      statusEl.textContent = text;
      statusEl.classList.toggle('wg-final', Boolean(isFinal));
    }

    function stop() {
      round++;
      timers.forEach(clearTimeout);
      timers = [];
      boardEl.innerHTML = '';
      boardEl.className = 'wg-board';
      boardEl.removeAttribute('data-game');
      boardEl.removeAttribute('data-sequence');
      setStatus('');
    }

    function begin(name) {
      stop();
      current = name;
      var thisRound = round;
      boardEl.classList.add('wg-board-' + name);
      boardEl.dataset.game = name;

      IMPLEMENTATIONS[name]({
        board: boardEl,
        status: function (text, isFinal) { if (thisRound === round) setStatus(text, isFinal); },
        later: function (fn, ms) {
          var id = setTimeout(function () {
            timers = timers.filter(function (t) { return t !== id; });
            if (thisRound === round) fn();
          }, ms);
          timers.push(id);
        }
      });
    }

    return {
      newGame: function () { begin(pickGame()); },            // a new wait: a different game
      restart: function () { begin(current || pickGame()); }, // "New game": the same game again
      stop: stop
    };
  }

  // ── Modal host (typed "Check answer" / "Check my work" flows) ───────────────
  var modal;          // Bootstrap modal instance
  var host;           // controller bound to the modal's board
  var els = {};       // cached modal nodes
  var revealFn;       // callback to run when the student asks to see the result

  document.addEventListener('DOMContentLoaded', function () {
    var root = document.getElementById('waitingGameModal');
    if (!root || typeof bootstrap === 'undefined') return;   // markup or Bootstrap missing — feature off

    modal = new bootstrap.Modal(root, { backdrop: 'static', keyboard: false });

    els.checking = root.querySelector('.wg-checking');
    els.ready    = root.querySelector('.wg-ready');
    els.reveal   = root.querySelector('.wg-reveal-btn');
    els.newGame  = root.querySelector('.wg-newgame-btn');

    host = createController(root.querySelector('.wg-board'), root.querySelector('.wg-game-status'));

    // Whatever closes the window, no old move may survive into the next round.
    root.addEventListener('hidden.bs.modal', function () { host.stop(); });

    els.newGame.addEventListener('click', function () { host.restart(); });
    els.reveal.addEventListener('click', function () {
      var fn = revealFn;
      revealFn = null;
      host.stop();          // end the round now, not when the fade-out finishes
      modal.hide();
      if (typeof fn === 'function') fn();
    });
  });

  var WaitingGame = {
    start: function () {
      if (!modal) return;
      revealFn = null;
      els.checking.classList.remove('d-none');
      els.ready.classList.add('d-none');
      host.newGame();
      modal.show();
    },

    // The verdict is in. Keep the game playable but surface a way out.
    finish: function (reveal) {
      if (!modal) { if (typeof reveal === 'function') reveal(); return; }
      revealFn = reveal;
      els.checking.classList.add('d-none');
      els.ready.classList.remove('d-none');
    },

    close: function () {
      revealFn = null;
      if (host) host.stop();
      if (modal) modal.hide();
    },

    // Mount the games into caller-owned elements; returns a controller to start, restart and stop them.
    attach: function (boardEl, statusEl) {
      if (!boardEl) return { newGame: function () {}, restart: function () {}, stop: function () {} };
      return createController(boardEl, statusEl);
    },

    games: GAMES.slice(),

    // Chooses the game for the next wait. Used by the end-to-end tests to cover all three games.
    setNextGame: function (name) {
      if (GAMES.indexOf(name) === -1) throw new Error('Unknown game: ' + name);
      forcedNext = name;
    }
  };

  window.WaitingGame = WaitingGame;
})();

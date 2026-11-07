/*
 * Waiting game — a round of tic-tac-toe to pass the time while the server checks
 * an answer, so a slow AI call feels like a moment of play instead of a frozen
 * spinner. The student may keep playing after the answer lands; the check runs on
 * its own regardless of what they tap here.
 *
 * Two ways to host the game:
 *
 *   WaitingGame.start() / .finish(reveal) / .close()
 *       Drives a built-in modal, used where the wait has no UI of its own (the
 *       typed "Check answer" button). start() opens it with a fresh board;
 *       finish() keeps the board playable but offers a "See result" button that
 *       runs `reveal` and closes; close() dismisses without a result.
 *
 *   WaitingGame.attach(boardEl, statusEl) → { newGame() }
 *       Mounts the same game into a caller's own elements, used where the wait
 *       already has a place to live (the photo-solution loading stage sits inside
 *       its own modal, so a second modal would stack). The caller deals a fresh
 *       board with newGame() whenever a wait begins.
 */
(function () {
  'use strict';

  var PLAYER = 'X';   // the student always moves first
  var CPU    = 'O';

  // The eight lines that win a game of tic-tac-toe, as board-cell index triples.
  var LINES = [
    [0, 1, 2], [3, 4, 5], [6, 7, 8],   // rows
    [0, 3, 6], [1, 4, 7], [2, 5, 8],   // columns
    [0, 4, 8], [2, 4, 6]               // diagonals
  ];

  // ── Game engine ────────────────────────────────────────────────────────────
  // One instance owns one board element and its status line. Everything below
  // closes over `boardEl`/`statusEl`, so several games can run independently.
  function createGame(boardEl, statusEl) {
    var board;    // 9-cell array: 'X', 'O', or ''
    var locked;   // true while it is the computer's turn or the game is over

    buildCells();

    function buildCells() {
      boardEl.innerHTML = '';
      for (var i = 0; i < 9; i++) {
        var cell = document.createElement('button');
        cell.type = 'button';
        cell.className = 'wg-cell';
        cell.dataset.i = i;
        cell.setAttribute('aria-label', 'Square ' + (i + 1));
        cell.addEventListener('click', onCellClick);
        boardEl.appendChild(cell);
      }
    }

    function newGame() {
      board = ['', '', '', '', '', '', '', '', ''];
      locked = false;
      boardEl.querySelectorAll('.wg-cell').forEach(function (cell) {
        cell.textContent = '';
        cell.disabled = false;
        cell.classList.remove('wg-x', 'wg-o', 'wg-win');
      });
      setStatus('Your turn — you are X');
    }

    function onCellClick(e) {
      var i = parseInt(e.currentTarget.dataset.i, 10);
      if (locked || board[i]) return;

      place(i, PLAYER);
      if (endIfOver()) return;

      // Hand the turn to the computer, with a short beat so moves feel taken.
      locked = true;
      setStatus('Computer is thinking…');
      setTimeout(cpuMove, 380);
    }

    function cpuMove() {
      var i = chooseCpuMove();
      if (i !== -1) place(i, CPU);
      if (endIfOver()) return;

      locked = false;
      setStatus('Your turn');
    }

    function endIfOver() {
      var win = winningLine(board);
      if (win) {
        win.forEach(function (i) { cellAt(i).classList.add('wg-win'); });
        locked = true;
        setStatus(board[win[0]] === PLAYER ? 'You win! 🎉' : 'Computer wins — try again', true);
        return true;
      }
      if (board.every(Boolean)) {
        locked = true;
        setStatus("It's a draw", true);
        return true;
      }
      return false;
    }

    function place(i, mark) {
      board[i] = mark;
      var cell = cellAt(i);
      cell.textContent = mark;
      cell.classList.add(mark === PLAYER ? 'wg-x' : 'wg-o');
      cell.disabled = true;
    }

    function cellAt(i) {
      return boardEl.querySelector('.wg-cell[data-i="' + i + '"]');
    }

    function setStatus(text, isFinal) {
      if (!statusEl) return;
      statusEl.textContent = text;
      statusEl.classList.toggle('wg-final', Boolean(isFinal));
    }

    // Deliberately beatable: take a win, block a loss, then prefer centre and
    // corners. A student who plays well can win, which is the point of the break.
    function chooseCpuMove() {
      var win = findLine(CPU);       // complete three in a row if possible
      if (win !== -1) return win;

      var block = findLine(PLAYER);  // otherwise deny the student their win
      if (block !== -1) return block;

      if (!board[4]) return 4;       // centre

      var corners = [0, 2, 6, 8].filter(function (i) { return !board[i]; });
      if (corners.length) return corners[Math.floor(Math.random() * corners.length)];

      var free = [];
      for (var i = 0; i < 9; i++) if (!board[i]) free.push(i);
      return free.length ? free[Math.floor(Math.random() * free.length)] : -1;
    }

    // The empty cell that would give `mark` a completed line, or -1 if none.
    function findLine(mark) {
      for (var l = 0; l < LINES.length; l++) {
        var line = LINES[l];
        var marks = 0, empty = -1;
        for (var k = 0; k < 3; k++) {
          if (board[line[k]] === mark) marks++;
          else if (!board[line[k]]) empty = line[k];
        }
        if (marks === 2 && empty !== -1) return empty;
      }
      return -1;
    }

    return { newGame: newGame };
  }

  function winningLine(b) {
    for (var l = 0; l < LINES.length; l++) {
      var line = LINES[l];
      if (b[line[0]] && b[line[0]] === b[line[1]] && b[line[1]] === b[line[2]]) return line;
    }
    return null;
  }

  // ── Modal host (typed "Check answer" flow) ──────────────────────────────────
  var modal;          // Bootstrap modal instance
  var game;           // engine bound to the modal's board
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

    game = createGame(root.querySelector('.wg-board'), root.querySelector('.wg-game-status'));

    els.newGame.addEventListener('click', function () { game.newGame(); });
    els.reveal.addEventListener('click', function () {
      var fn = revealFn;
      revealFn = null;
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
      game.newGame();
      modal.show();
    },

    // The verdict is in. Keep the board playable but surface a way out.
    finish: function (reveal) {
      if (!modal) { if (typeof reveal === 'function') reveal(); return; }
      revealFn = reveal;
      els.checking.classList.add('d-none');
      els.ready.classList.remove('d-none');
    },

    close: function () {
      revealFn = null;
      if (modal) modal.hide();
    },

    // Mount the game into caller-owned elements; returns a controller to reset it.
    attach: function (boardEl, statusEl) {
      if (!boardEl) return { newGame: function () {} };
      return createGame(boardEl, statusEl);
    }
  };

  window.WaitingGame = WaitingGame;
})();

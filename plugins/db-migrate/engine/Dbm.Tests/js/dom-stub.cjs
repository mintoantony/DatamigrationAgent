'use strict';
/*
 * Ruling 141: the render layer gets tested, and the way it gets tested is a DOM stub - no jsdom, no npm package, no refactor of
 * the views. This is the smallest `document` that `DBM.h`, `components/core.js` and the two T5.6 views actually use.
 *
 * It is deliberately minimal, and the one semantic that matters most is textContent: children are concatenated with **no
 * separator**, exactly as a browser's innerText runs adjacent inline elements together. That is what makes "Runningno key"
 * visible to a test instead of only to a screen reader. Keep it minimal, and keep the browser pass: a stub that drifts from real
 * DOM semantics passes a test the browser would fail.
 *
 * Open item 25: this is the suite's one DOM stub. mapping-edits.test.cjs (T3.5) used to carry a private fake DOM of its own, whose
 * classList did nothing and whose `disabled` never followed the attribute; it now installs this one. A third stub is how fidelity
 * drifts - extend this file instead. The one named exception (review N2) is sql-view.test.cjs's fakeDom(): it builds no `document`,
 * it swaps DBM.h for a plain-object tree its render tests assert against; one-dom-stub.test.cjs lists it and fails on any other.
 */

/** Boolean content attributes whose IDL property the browser keeps in step with the attribute. */
const REFLECTED = ['disabled', 'hidden', 'checked', 'readonly', 'required'];

class FakeNode {
  constructor(tag, ns) {
    this.tagName = tag;
    this.namespace = ns || null;
    this.children = [];
    this.attributes = {};
    this.dataset = {};
    this.listeners = {};
    this.parentNode = null;
    this.text = '';
    this.hidden = false;
    this.disabled = false;
    this.value = '';
    this.checked = false;
    this.scrollTop = 0;
    this.scrollHeight = 0;
    this._className = '';
    const self = this;
    this.styles = {};
    this.style = {
      setProperty(k, v) { self.styles[k] = String(v); },
      getPropertyValue(k) { return self.styles[k] === undefined ? '' : self.styles[k]; },
    };
    this.classList = {
      add(c) { if (!self.classes().includes(c)) self.className = (self._className + ' ' + c).trim(); },
      remove(c) { self.className = self.classes().filter((x) => x !== c).join(' '); },
      contains(c) { return self.classes().includes(c); },
      toggle(c, on) { const has = self.classes().includes(c); if (on === undefined ? has : !on) this.remove(c); else this.add(c); },
    };
  }

  get className() { return this._className; }
  set className(v) { this._className = Array.isArray(v) ? v.filter(Boolean).join(' ') : String(v == null ? '' : v); }
  classes() { return this._className.split(/\s+/).filter(Boolean); }

  appendChild(c) { c.parentNode = this; this.children.push(c); return c; }
  removeChild(c) { this.children.splice(this.children.indexOf(c), 1); c.parentNode = null; return c; }
  replaceChild(n, o) { const i = this.children.indexOf(o); this.children[i] = n; n.parentNode = this; o.parentNode = null; return o; }
  remove() { if (this.parentNode) this.parentNode.removeChild(this); }
  get firstChild() { return this.children[0] || null; }

  /** No separator between children: adjacent inline elements run together here exactly as innerText does in a browser. */
  get textContent() { return this.text + this.children.map((c) => c.textContent).join(''); }
  set textContent(v) { this.children = []; this.text = String(v); }

  /* The browser reflects the boolean content attributes onto the IDL properties, and DBM.h writes the attribute
     (`disabled: true` -> setAttribute('disabled', '')). Without the reflection a test reads an armed button where the
     browser shows a dead one - the drift Ruling 141 warns about, found by exactly that assertion. */
  setAttribute(k, v) {
    this.attributes[k] = String(v);
    if (k === 'value') this.value = String(v);
    if (REFLECTED.indexOf(k) >= 0) this[k] = true;
  }
  getAttribute(k) { return this.attributes[k] === undefined ? null : this.attributes[k]; }
  removeAttribute(k) { delete this.attributes[k]; if (REFLECTED.indexOf(k) >= 0) this[k] = false; }
  addEventListener(ev, fn) { (this.listeners[ev] = this.listeners[ev] || []).push(fn); }
  removeEventListener(ev, fn) { this.listeners[ev] = (this.listeners[ev] || []).filter((f) => f !== fn); }
  querySelector(sel) { return query(this, sel); }
  querySelectorAll(sel) { return queryAll(this, sel); }
  closest() { return null; }
  scrollIntoView() {}
  focus() {}
  /** Fires a listener the way a click or a keypress would. */
  fire(ev, extra) {
    const e = Object.assign({ type: ev, target: this, currentTarget: this, preventDefault() {}, stopPropagation() {} }, extra || {});
    (this.listeners[ev] || []).forEach((fn) => fn(e));
    return e;
  }
}

/** Supported selectors: `tag`, `.class`, `tag.class`, `[attr]`, `[attr=value]`, and a descendant chain of those. */
function matches(node, sel) {
  return sel.split(/(?=[.[])/).every((part) => {
    if (part.startsWith('.')) return node.classes().includes(part.slice(1));
    if (part.startsWith('[')) {
      const body = part.slice(1, -1);
      const eq = body.indexOf('=');
      if (eq < 0) return node.getAttribute(body) !== null || node[body] === true;
      return node.getAttribute(body.slice(0, eq)) === body.slice(eq + 1);
    }
    return node.tagName === part;
  });
}

function queryAll(root, selector) {
  const steps = String(selector).trim().split(/\s+(?![^[]*\])/);
  let level = [root];
  steps.forEach((step) => {
    const next = [];
    level.forEach((node) => collect(node, step, next, node === root));
    level = next;
  });
  return level;
}

function collect(node, step, out, skipSelf) {
  if (!skipSelf && matches(node, step) && out.indexOf(node) < 0) out.push(node);
  node.children.forEach((c) => collect(c, step, out, false));
}

function query(root, selector) { return queryAll(root, selector)[0] || null; }

function find(node, pred) {
  if (pred(node)) return node;
  for (const c of node.children) { const hit = find(c, pred); if (hit) return hit; }
  return null;
}

/** The visible text of a subtree, with children run together exactly as the browser would. */
function text(node) { return node ? node.textContent : ''; }

/** One line per element matching the selector - what an operator reads down a list or a column. */
function texts(root, selector) { return queryAll(root, selector).map((n) => n.textContent); }

/**
 * Installs the globals the classic scripts expect. requestAnimationFrame is queued rather than timed, so a test can assert how
 * many frames a burst of events cost and flush them deliberately.
 */
function install() {
  const ids = {};
  ['app', 'topbar', 'banners', 'stepper', 'view', 'drawer', 'toasts'].forEach((id) => { ids[id] = new FakeNode('div'); });
  const frames = [];
  globalThis.window = globalThis;
  globalThis.document = {
    readyState: 'complete',
    title: '',
    activeElement: null,
    documentElement: new FakeNode('html'),
    body: new FakeNode('body'),
    createElement: (tag) => new FakeNode(tag),
    createElementNS: (ns, tag) => new FakeNode(tag, ns),
    createTextNode: (t) => { const n = new FakeNode('#text'); n.text = String(t); return n; },
    getElementById: (id) => ids[id] || null,
    addEventListener() {},
    removeEventListener() {},
  };
  globalThis.location = { hash: '', search: '' };
  globalThis.history = { replaceState() {} };
  globalThis.addEventListener = () => {};
  globalThis.scrollY = 0;
  globalThis.scrollTo = () => {};
  globalThis.requestAnimationFrame = (fn) => frames.push(fn);
  globalThis.cancelAnimationFrame = () => {};
  if (!globalThis.localStorage) {
    Object.defineProperty(globalThis, 'localStorage', { value: { getItem: () => null, setItem() {} }, configurable: true });
  }
  return {
    ids,
    /** Runs the frames queued since the last flush and answers how many there were - one per burst is the rule under test. */
    flushFrames() { const n = frames.length; frames.splice(0).forEach((fn) => fn()); return n; },
    pendingFrames() { return frames.length; },
  };
}

/** Lets a pending promise chain (and, with ms, a setTimeout) run before the assertions. */
function settle(ms) { return new Promise((r) => setTimeout(r, ms || 0)); }

module.exports = { install, FakeNode, query, queryAll, find, text, texts, matches, settle };

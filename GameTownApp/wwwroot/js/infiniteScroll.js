// The library's endless scroll: tells .NET when the end of the shelf comes into view.
//
// An IntersectionObserver on an empty sentinel after the last tile, rather than a scroll listener
// doing arithmetic on every frame. The page itself scrolls (not an inner container), so the
// viewport is the root. The margin starts the next batch about a screen early, so a normal scroll
// never reaches a blank gap.

const observers = new WeakMap();

/**
 * @param {Element} sentinel  element placed after the last tile
 * @param {object}  dotNetRef receives LoadMore()
 * @param {string}  rootMargin how far ahead of the viewport to trigger
 */
export function observe(sentinel, dotNetRef, rootMargin) {
    unobserve(sentinel);
    const observer = new IntersectionObserver(entries => {
        if (entries.some(e => e.isIntersecting)) {
            dotNetRef.invokeMethodAsync("LoadMore");
        }
    }, { rootMargin: rootMargin || "800px 0px" });
    observer.observe(sentinel);
    observers.set(sentinel, observer);
}

/**
 * Re-checks the sentinel. An observer only fires on a CHANGE in intersection, so when a batch lands
 * and the sentinel is still on screen (a tall window, a short batch) nothing would ever fire again.
 */
export function recheck(sentinel, dotNetRef) {
    if (!sentinel) return;
    const rect = sentinel.getBoundingClientRect();
    if (rect.top < window.innerHeight + 800) {
        dotNetRef.invokeMethodAsync("LoadMore");
    }
}

export function unobserve(sentinel) {
    const observer = sentinel && observers.get(sentinel);
    if (observer) {
        observer.disconnect();
        observers.delete(sentinel);
    }
}

export function scrollY() {
    return window.scrollY;
}

export function scrollTo(y) {
    window.scrollTo({ top: y, behavior: "instant" });
}

/**
 * Puts the reader back where they were on the shelf. Repeated over the next two frames because
 * FocusOnNavigate focuses the page's <h1> after a route change, and focusing scrolls it into view —
 * which, done after us, would undo the restore.
 */
export function restoreScroll(y) {
    scrollTo(y);
    requestAnimationFrame(() => {
        scrollTo(y);
        requestAnimationFrame(() => scrollTo(y));
    });
}

(() => {
  'use strict';

  const sidebar = document.querySelector('aside.sidebar');
  const topbar = document.querySelector('.topbar');
  const breadcrumbs = topbar?.querySelector('.breadcrumbs');
  if (!sidebar || !topbar || !breadcrumbs) return;

  const root = document.documentElement;
  const storageKey = 'smartping.sidebar.collapsed';
  const mobile = window.matchMedia('(max-width: 680px)');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const svgNamespace = 'http://www.w3.org/2000/svg';
  let collapsed = false;
  let resizeTimer;

  try {
    collapsed = localStorage.getItem(storageKey) === 'true';
  } catch { /* The control remains usable when browser storage is unavailable. */ }

  if (!sidebar.id) sidebar.id = 'smartping-sidebar';

  function createToggle(className) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `sidebar-toggle ${className}`;
    button.setAttribute('aria-controls', sidebar.id);

    const icon = document.createElementNS(svgNamespace, 'svg');
    icon.setAttribute('viewBox', '0 0 24 24');
    icon.setAttribute('fill', 'none');
    icon.setAttribute('stroke', 'currentColor');
    icon.setAttribute('stroke-width', '1.8');
    icon.setAttribute('stroke-linecap', 'round');
    icon.setAttribute('stroke-linejoin', 'round');
    icon.setAttribute('aria-hidden', 'true');

    const frame = document.createElementNS(svgNamespace, 'path');
    frame.setAttribute('d', 'M8 4H4v16h4');
    const arrow = document.createElementNS(svgNamespace, 'path');
    arrow.classList.add('sidebar-toggle-arrow');
    icon.append(frame, arrow);
    button.append(icon);
    button.addEventListener('click', () => setCollapsed(!collapsed, true));
    return button;
  }

  const closeButton = createToggle('sidebar-collapse');
  const closeLabel = document.createElement('span');
  closeLabel.className = 'sidebar-toggle-label';
  closeLabel.textContent = '收起侧边栏';
  closeButton.append(closeLabel);
  const brand = sidebar.querySelector('.brand');
  if (brand) brand.after(closeButton);
  else sidebar.prepend(closeButton);

  const topbarButton = createToggle('sidebar-reopen');
  const leading = document.createElement('div');
  leading.className = 'topbar-leading';
  breadcrumbs.before(leading);
  leading.append(topbarButton, breadcrumbs);

  function syncControls() {
    root.dataset.sidebarCollapsed = String(collapsed);
    sidebar.inert = collapsed;
    if (collapsed) sidebar.setAttribute('aria-hidden', 'true');
    else sidebar.removeAttribute('aria-hidden');

    const label = collapsed ? '展开侧边栏' : '收起侧边栏';
    for (const button of [closeButton, topbarButton]) {
      button.setAttribute('aria-expanded', String(!collapsed));
      button.setAttribute('aria-label', label);
      button.title = label;
      button.querySelector('.sidebar-toggle-arrow').setAttribute('d', collapsed
        ? 'M12 7l5 5-5 5M9 12h8'
        : 'M16 7l-5 5 5 5M11 12h8');
    }
    topbarButton.hidden = !collapsed && !mobile.matches;
  }

  function setCollapsed(value, focusControl = false) {
    collapsed = value;
    syncControls();
    try {
      localStorage.setItem(storageKey, String(collapsed));
    } catch { /* A private or restricted browser session may deny storage. */ }

    if (focusControl) {
      (collapsed || mobile.matches ? topbarButton : closeButton).focus({ preventScroll: true });
    }
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => window.dispatchEvent(new Event('resize')), reducedMotion.matches ? 0 : 240);
  }

  mobile.addEventListener('change', () => {
    const restoreFocus = document.activeElement === topbarButton;
    syncControls();
    if (restoreFocus && topbarButton.hidden) closeButton.focus({ preventScroll: true });
  });
  window.addEventListener('storage', event => {
    if (event.key !== storageKey) return;
    const restoreFocus = sidebar.contains(document.activeElement) || document.activeElement === topbarButton;
    setCollapsed(event.newValue === 'true', restoreFocus);
  });

  syncControls();
})();

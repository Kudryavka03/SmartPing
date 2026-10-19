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

  const rail = document.createElement('nav');
  rail.className = 'sidebar-rail';
  rail.setAttribute('aria-label', '快捷导航');
  const edgeButton = createToggle('sidebar-rail-expand');
  rail.append(edgeButton);
  for (const source of sidebar.querySelectorAll('nav .nav-link')) {
    const shortcut = document.createElement('a');
    shortcut.className = `nav-link sidebar-rail-link${source.classList.contains('active') ? ' active' : ''}`;
    shortcut.setAttribute('href', source.getAttribute('href'));
    shortcut.dataset.view = source.dataset.view;
    const label = [...source.childNodes].filter(node => node.nodeType === 3)
      .map(node => node.textContent).join('').trim();
    shortcut.title = label;
    shortcut.setAttribute('aria-label', label);
    if (source.getAttribute('aria-current')) shortcut.setAttribute('aria-current', source.getAttribute('aria-current'));
    const sourceIcon = source.querySelector('svg');
    if (sourceIcon) shortcut.append(sourceIcon.cloneNode(true));
    rail.append(shortcut);
  }
  document.body.append(rail);

  function expandedFocusControl() {
    return mobile.matches ? sidebar.querySelector('.nav-link') || topbarButton : closeButton;
  }

  function syncControls() {
    root.dataset.sidebarCollapsed = String(collapsed);
    sidebar.inert = collapsed;
    if (collapsed) sidebar.setAttribute('aria-hidden', 'true');
    else sidebar.removeAttribute('aria-hidden');

    const label = collapsed ? '展开侧边栏' : '收起侧边栏';
    for (const button of [closeButton, topbarButton, edgeButton]) {
      button.setAttribute('aria-expanded', String(!collapsed));
      button.setAttribute('aria-label', label);
      button.title = label;
      button.querySelector('.sidebar-toggle-arrow').setAttribute('d', collapsed
        ? 'M12 7l5 5-5 5M9 12h8' : 'M16 7l-5 5 5 5M11 12h8');
    }
    topbarButton.hidden = collapsed || !mobile.matches;
    rail.hidden = !collapsed;
    rail.inert = !collapsed;
  }

  function setCollapsed(value, focusControl = false) {
    collapsed = value;
    syncControls();
    try {
      localStorage.setItem(storageKey, String(collapsed));
    } catch { /* A private or restricted browser session may deny storage. */ }

    if (focusControl) {
      (collapsed ? edgeButton : expandedFocusControl()).focus({ preventScroll: true });
    }
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => window.dispatchEvent(new Event('resize')), reducedMotion.matches ? 0 : 240);
  }

  mobile.addEventListener('change', () => {
    const activeControl = document.activeElement;
    syncControls();
    if ((activeControl === topbarButton && topbarButton.hidden)
      || (activeControl === closeButton && mobile.matches)) {
      (collapsed ? edgeButton : expandedFocusControl()).focus({ preventScroll: true });
    }
  });
  window.addEventListener('storage', event => {
    if (event.key !== storageKey) return;
    const restoreFocus = sidebar.contains(document.activeElement)
      || document.activeElement === topbarButton || rail.contains(document.activeElement);
    setCollapsed(event.newValue === 'true', restoreFocus);
  });

  syncControls();
})();

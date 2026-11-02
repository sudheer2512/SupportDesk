/*
 * @sd/ui — SupportDesk design system. Standalone, OnPush, signal-based components (selector prefix `sd-`).
 * Global styles: projects/ui/src/styles/sd-ui.css (tokens in sd-tokens.css).
 */

// Primitives
export * from './lib/icon/icon.component';
export * from './lib/icon/icon-registry';
export * from './lib/button/button.component';
export * from './lib/spinner/spinner.component';
export * from './lib/badge/badge.component';
export * from './lib/avatar/avatar.component';
export * from './lib/card/card.component';
export * from './lib/page-header/page-header.component';
export * from './lib/tabs/tabs.component';
export * from './lib/menu/dropdown-menu.component';
export * from './lib/tooltip/tooltip.directive';
export * from './lib/empty-state/empty-state.component';
export * from './lib/skeleton/skeleton.component';
export * from './lib/logo/logo.component';

// Overlays & feedback
export * from './lib/toast/toast.service';
export * from './lib/toast/toast-outlet.component';
export * from './lib/dialog/dialog.component';
export * from './lib/dialog/dialog.service';
export * from './lib/drawer/drawer.component';
export * from './lib/command-palette/command-palette.service';
export * from './lib/command-palette/command-palette.component';
export * from './lib/shortcut-help/shortcut-help.component';

// Forms
export * from './lib/forms/form-field.component';
export * from './lib/forms/control-base';
export * from './lib/forms/checkbox.component';
export * from './lib/forms/radio.component';
export * from './lib/forms/combobox.component';
export * from './lib/forms/date-input.component';
export * from './lib/forms/chips-input.component';

// Data
export * from './lib/table/data-table.types';
export * from './lib/table/data-table.component';
export * from './lib/filter-builder/filter-builder.component';
export * from './lib/timeline/timeline.component';
export * from './lib/kpi/kpi-tile.component';
export * from './lib/calendar/calendar.component';
export * from './lib/sla/sla-countdown.component';
export * from './lib/json-viewer/json-viewer.component';
export * from './lib/qr/qr-code.component';

// Heavy, lazily used widgets (TipTap / ECharts) — tree-shaken out of bundles that do not use them.
export * from './lib/editor/rich-text-editor.component';
export * from './lib/editor/sanitize-html';
export * from './lib/chart/chart.component';

// Layout
export * from './lib/layout/split-layout.component';

// Pipes & directives
export * from './lib/pipes/label.pipe';
export * from './lib/pipes/time.pipes';
export * from './lib/pipes/prose-html.pipe';
export * from './lib/permissions/if-can.directive';

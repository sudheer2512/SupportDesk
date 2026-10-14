/**
 * Component style fragments shared by the Customers and Products & Assets screens (added to a component's `styles`
 * array; tokens only). `.dt` is a compact, responsive data table for short lists inside cards and tabs.
 */
export const TABLE_STYLES = `
  .dt-wrap {
    overflow-x: auto;
    border: 1px solid var(--sd-border);
    border-radius: var(--sd-radius-md);
    background: var(--sd-surface);
  }
  .dt {
    width: 100%;
    border-collapse: collapse;
    font-size: var(--sd-text-sm);
  }
  .dt th {
    text-align: left;
    font-weight: var(--sd-weight-medium);
    color: var(--sd-text-3);
    background: var(--sd-surface-2);
    padding: 8px 12px;
    border-bottom: 1px solid var(--sd-border);
    white-space: nowrap;
  }
  .dt td {
    padding: 8px 12px;
    border-bottom: 1px solid var(--sd-divider);
    vertical-align: middle;
  }
  .dt tr:last-child td {
    border-bottom: 0;
  }
  .dt tbody tr.clickable {
    cursor: pointer;
  }
  .dt tbody tr.clickable:hover,
  .dt tbody tr.clickable:focus-visible {
    background: var(--sd-row-hover);
    outline: none;
  }
  .dt .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .dt .nowrap {
    white-space: nowrap;
  }
  .dt a {
    color: var(--sd-primary);
    text-decoration: none;
  }
  .dt a:hover {
    text-decoration: underline;
  }
`;

export const SECTION_STYLES = `
  .cards {
    display: grid;
    gap: var(--sd-space-4);
    grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr));
  }
  .kpis {
    display: grid;
    gap: var(--sd-space-3);
    grid-template-columns: repeat(auto-fill, minmax(min(100%, 170px), 1fr));
    margin-bottom: var(--sd-space-4);
  }
  .section-head {
    display: flex;
    align-items: center;
    gap: var(--sd-space-2);
    margin: var(--sd-space-5) 0 var(--sd-space-2);
  }
  .section-head h2 {
    font-size: var(--sd-text-md);
    font-weight: var(--sd-weight-semibold);
  }
  .meta {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: 6px var(--sd-space-3);
    font-size: var(--sd-text-sm);
  }
  .meta dt {
    color: var(--sd-text-3);
  }
  .meta dd {
    margin: 0;
    min-width: 0;
    overflow-wrap: anywhere;
  }
  .bar {
    position: relative;
    height: 6px;
    border-radius: var(--sd-radius-pill);
    background: var(--sd-surface-3);
    overflow: hidden;
    min-width: 80px;
  }
  .bar > span {
    position: absolute;
    inset: 0 auto 0 0;
    background: var(--sd-primary);
    border-radius: inherit;
  }
  .bar.is-warn > span {
    background: var(--sd-warning);
  }
  .bar.is-danger > span {
    background: var(--sd-danger);
  }
  .empty-note {
    color: var(--sd-text-3);
    font-size: var(--sd-text-sm);
    padding: var(--sd-space-3) 0;
  }
  .row-actions {
    display: flex;
    gap: 4px;
    justify-content: flex-end;
  }
`;

import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActionParamDto, ActionTypeDto, JsonValue, RuleActionDef } from '@sd/core';
import { SdChipsInput, SdCombobox, SdFormField, SdInput, SdOption, SdToggle } from '@sd/ui';
import { AutomationContext } from './automation-context.service';
import { IssueMap } from './automation-ui';

/**
 * Schema-driven parameter editor for one automation action: renders the right picker for each parameter type
 * of the server catalog (agents, teams, statuses, categories, templates, tags, placeholders, HTTP headers, JSON…).
 * `<desk-action-params [action]="a" [spec]="spec" [issues]="issues" (changed)="update($event)" />`
 */
@Component({
  selector: 'desk-action-params',
  imports: [FormsModule, SdFormField, SdInput, SdCombobox, SdChipsInput, SdToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="params">
      @for (p of spec().params; track p.name) {
        <sd-form-field
          [label]="p.label"
          [hint]="hintFor(p)"
          [error]="issues()['params.' + p.name]"
          [required]="p.required"
          [class.wide]="isWide(p)"
        >
          @switch (editorFor(p)) {
            @case ('select') {
              <sd-combobox [options]="optionsFor(p)" [value]="str(p.name)" (valueChange)="set(p.name, $any($event))" [placeholder]="'Choose ' + p.label.toLowerCase()" />
            }
            @case ('multi') {
              <sd-combobox multiple [options]="optionsFor(p)" [value]="list(p.name)" (valueChange)="set(p.name, $any($event) ?? [])" placeholder="Choose…" />
            }
            @case ('enum') {
              <select sdInput [ngModel]="str(p.name) ?? ''" (ngModelChange)="set(p.name, $event || null)">
                @if (!p.required) {
                  <option value="">—</option>
                }
                @for (o of p.options ?? []; track o) {
                  <option [value]="o">{{ o }}</option>
                }
              </select>
            }
            @case ('substatus') {
              <select sdInput [ngModel]="str(p.name) ?? ''" (ngModelChange)="set(p.name, $event || null)">
                <option value="">—</option>
                @for (o of substatuses(); track o) {
                  <option [value]="o">{{ o }}</option>
                }
              </select>
            }
            @case ('tags') {
              <sd-chips-input lowercase [suggestions]="ctx.tags()" [value]="list(p.name)" (valueChange)="set(p.name, $event)" placeholder="Add tag" />
            }
            @case ('bool') {
              <sd-toggle [value]="bool(p.name)" (valueChange)="set(p.name, $event)" [ariaLabel]="p.label">{{ bool(p.name) ? 'Yes' : 'No' }}</sd-toggle>
            }
            @case ('int') {
              <input sdInput type="number" [ngModel]="action().params?.[p.name] ?? null" (ngModelChange)="set(p.name, toNumber($event))" />
            }
            @case ('template') {
              <textarea
                sdInput
                #ta
                [rows]="p.name === 'body' || p.name === 'bodyHtml' || p.name === 'message' ? 4 : 2"
                [ngModel]="str(p.name) ?? ''"
                (ngModelChange)="set(p.name, $event)"
              ></textarea>
              <div class="placeholders" aria-label="Insert placeholder">
                @for (ph of placeholders(); track ph) {
                  <button type="button" class="ph" (click)="insert(ta, p.name, ph)">{{ ph }}</button>
                }
              </div>
            }
            @case ('headers') {
              <textarea
                sdInput
                rows="3"
                class="mono"
                placeholder="Authorization: Bearer …&#10;X-Source: SupportDesk"
                [ngModel]="headersText(p.name)"
                (ngModelChange)="setHeaders(p.name, $event)"
              ></textarea>
            }
            @case ('json') {
              <textarea sdInput rows="4" class="mono" [ngModel]="jsonText(p.name)" (change)="setJson(p.name, $any($event.target).value)"></textarea>
            }
            @case ('secret') {
              @if (isEncrypted(p.name)) {
                <div class="secret">
                  <span class="sd-muted">Stored encrypted</span>
                  <button type="button" class="link" (click)="set(p.name, null)">Replace</button>
                </div>
              } @else {
                <input sdInput type="password" autocomplete="new-password" [ngModel]="str(p.name) ?? ''" (ngModelChange)="set(p.name, $event || null)" />
              }
            }
            @case ('cfvalue') {
              @switch (customFieldKind()) {
                @case ('bool') {
                  <sd-toggle [value]="bool(p.name)" (valueChange)="set(p.name, $event)" ariaLabel="Value">{{ bool(p.name) ? 'Checked' : 'Unchecked' }}</sd-toggle>
                }
                @case ('number') {
                  <input sdInput type="number" [ngModel]="action().params?.[p.name] ?? null" (ngModelChange)="set(p.name, toNumber($event))" />
                }
                @default {
                  <input sdInput type="text" [ngModel]="str(p.name) ?? ''" (ngModelChange)="set(p.name, $event)" />
                }
              }
            }
            @default {
              <input sdInput type="text" [placeholder]="p.type === 'url' ? 'https://…' : ''" [ngModel]="str(p.name) ?? ''" (ngModelChange)="set(p.name, $event || null)" />
            }
          }
        </sd-form-field>
      }
      @if (spec().params.length === 0) {
        <p class="sd-muted none">No settings — this action runs as is.</p>
      }
    </div>
  `,
  styles: `
    .params {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 240px), 1fr));
      gap: 10px 14px;
    }
    .wide {
      grid-column: 1 / -1;
    }
    .placeholders {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin-top: 6px;
    }
    .ph {
      padding: 1px 6px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xs);
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
      cursor: pointer;
    }
    .ph:hover,
    .ph:focus-visible {
      border-color: var(--sd-primary-border);
      color: var(--sd-primary);
    }
    .mono {
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-sm);
    }
    .secret {
      display: flex;
      align-items: center;
      gap: 10px;
      min-height: var(--sd-control-h);
    }
    .link {
      border: 0;
      background: none;
      color: var(--sd-primary);
      cursor: pointer;
      padding: 0;
    }
    .none {
      font-size: var(--sd-text-sm);
    }
  `,
})
export class ActionParamsEditor {
  protected readonly ctx = inject(AutomationContext);

  readonly action = input.required<RuleActionDef>();
  readonly spec = input.required<ActionTypeDto>();
  /** Issues for this action, keyed relative to it ("params.url"). */
  readonly issues = input<IssueMap>({});
  readonly changed = output<RuleActionDef>();

  protected readonly placeholders = computed(() => this.ctx.catalog()?.templatePlaceholders ?? []);
  protected readonly substatuses = computed(() => this.ctx.substatusOptions(this.str('statusId')));
  protected readonly customFieldKind = computed(() => {
    const t = this.ctx.customFieldType(this.str('key'));
    if (t === 'Checkbox') return 'bool';
    if (t === 'Number' || t === 'Decimal' || t === 'Currency') return 'number';
    return 'text';
  });

  protected editorFor(p: ActionParamDto): string {
    if (p.name === 'substatus' && this.action().type === 'set_status') return 'substatus';
    if (p.name === 'value' && this.action().type === 'set_custom_field') return 'cfvalue';
    switch (p.type) {
      case 'user':
      case 'team':
      case 'department':
      case 'status':
      case 'category':
      case 'issueType':
      case 'role':
      case 'replyTemplate':
      case 'customField':
        return 'select';
      case 'users':
      case 'roles':
        return 'multi';
      case 'list':
        return 'multi';
      case 'enum':
        return 'enum';
      case 'tags':
        return 'tags';
      case 'bool':
        return 'bool';
      case 'int':
        return 'int';
      case 'template':
        return 'template';
      case 'headers':
        return 'headers';
      case 'json':
        return 'json';
      case 'secret':
        return 'secret';
      default:
        return 'text';
    }
  }

  protected isWide(p: ActionParamDto): boolean {
    return ['template', 'headers', 'json', 'url'].includes(p.type);
  }

  protected hintFor(p: ActionParamDto): string | null {
    if (p.type === 'template') return 'Supports {{placeholders}}.';
    if (p.type === 'headers') return 'One "Name: value" per line. Authorization/API-key values are stored encrypted.';
    if (p.type === 'json') return 'JSON object; string values support {{placeholders}}.';
    return p.description;
  }

  protected optionsFor(p: ActionParamDto): SdOption[] {
    const a = this.action();
    switch (p.type) {
      case 'user':
        return a.type === 'create_task' && p.name === 'assigneeId'
          ? [{ value: 'assignee', label: 'The ticket assignee' }, ...this.ctx.agentOptions()]
          : a.type === 'assign'
            ? this.ctx.assignableAgentOptions()
            : this.ctx.agentOptions();
      case 'users':
        return this.ctx.agentOptions();
      case 'team':
        return this.ctx.teamOptions();
      case 'department':
        return this.ctx.departmentOptions();
      case 'status':
        return this.ctx.statusOptions();
      case 'category':
        return p.name === 'subcategoryId' ? this.ctx.subcategoryOptions(this.str('categoryId')) : this.ctx.categoryOptions();
      case 'issueType':
        return this.ctx.issueTypeOptions();
      case 'role':
      case 'roles':
        return this.ctx.roleOptions();
      case 'replyTemplate':
        return this.ctx.templateOptions();
      case 'customField':
        return this.ctx.customFieldOptions();
      case 'list':
        return (p.options ?? []).map((o) => ({ value: o, label: o.charAt(0).toUpperCase() + o.slice(1) }));
      default:
        return [];
    }
  }

  protected str(name: string): string | null {
    const v = this.action().params?.[name];
    return v === null || v === undefined ? null : typeof v === 'string' ? v : String(v);
  }

  protected list(name: string): string[] {
    const v = this.action().params?.[name];
    return Array.isArray(v) ? v.map(String) : typeof v === 'string' && v ? v.split(',').map((s) => s.trim()) : [];
  }

  protected bool(name: string): boolean {
    return this.action().params?.[name] === true;
  }

  protected isEncrypted(name: string): boolean {
    return (this.str(name) ?? '').startsWith('enc:');
  }

  protected toNumber(raw: unknown): number | null {
    if (raw === null || raw === undefined || raw === '') return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }

  protected set(name: string, value: JsonValue | undefined): void {
    const a = this.action();
    const params: Record<string, JsonValue> = { ...(a.params ?? {}) };
    if (value === null || value === undefined || value === '' || (Array.isArray(value) && value.length === 0 && name !== 'tags')) delete params[name];
    else params[name] = value;
    // A new category invalidates the subcategory; a new status its substatus.
    if (name === 'categoryId') delete params['subcategoryId'];
    if (name === 'statusId') delete params['substatus'];
    this.changed.emit({ ...a, params });
  }

  protected insert(el: HTMLTextAreaElement, name: string, placeholder: string): void {
    const current = this.str(name) ?? '';
    const start = el.selectionStart ?? current.length;
    const end = el.selectionEnd ?? current.length;
    const next = current.slice(0, start) + placeholder + current.slice(end);
    this.set(name, next);
    queueMicrotask(() => {
      el.focus();
      el.setSelectionRange(start + placeholder.length, start + placeholder.length);
    });
  }

  protected headersText(name: string): string {
    const v = this.action().params?.[name];
    if (!v || typeof v !== 'object' || Array.isArray(v)) return '';
    return Object.entries(v)
      .map(([k, val]) => `${k}: ${String(val).startsWith('enc:') ? '•••••• (encrypted)' : String(val)}`)
      .join('\n');
  }

  protected setHeaders(name: string, text: string): void {
    const previous = this.action().params?.[name];
    const old = previous && typeof previous === 'object' && !Array.isArray(previous) ? (previous as Record<string, JsonValue>) : {};
    const out: Record<string, JsonValue> = {};
    for (const line of text.split('\n')) {
      const i = line.indexOf(':');
      if (i <= 0) continue;
      const key = line.slice(0, i).trim();
      let value = line.slice(i + 1).trim();
      // Keep the stored ciphertext when the masked value is left untouched.
      if (value === '•••••• (encrypted)' && typeof old[key] === 'string') value = old[key] as string;
      if (key) out[key] = value;
    }
    this.set(name, Object.keys(out).length ? out : null);
  }

  protected jsonText(name: string): string {
    const v = this.action().params?.[name];
    return v === undefined || v === null ? '' : JSON.stringify(v, null, 2);
  }

  protected setJson(name: string, text: string): void {
    if (!text.trim()) {
      this.set(name, null);
      return;
    }
    try {
      this.set(name, JSON.parse(text) as JsonValue);
    } catch {
      // Keep the raw text so the server reports "must be an object" and the user sees the problem.
      this.set(name, text);
    }
  }
}

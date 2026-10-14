import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  booleanAttribute,
  computed,
  effect,
  forwardRef,
  inject,
  input,
  model,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Editor, Extension } from '@tiptap/core';
import { CodeBlock } from '@tiptap/extension-code-block';
import { Image } from '@tiptap/extension-image';
import { Link } from '@tiptap/extension-link';
import { Mention } from '@tiptap/extension-mention';
import { Placeholder } from '@tiptap/extension-placeholder';
import { Table } from '@tiptap/extension-table';
import { TableCell } from '@tiptap/extension-table-cell';
import { TableHeader } from '@tiptap/extension-table-header';
import { TableRow } from '@tiptap/extension-table-row';
import { Youtube } from '@tiptap/extension-youtube';
import { StarterKit } from '@tiptap/starter-kit';
import { Observable, firstValueFrom, isObservable } from 'rxjs';
import { SdAvatar } from '../avatar/avatar.component';
import { SdControlBase } from '../forms/control-base';
import { SdIcon } from '../icon/icon.component';
import { SdDropdownMenu, SdMenuItem } from '../menu/dropdown-menu.component';
import { SdTooltip } from '../tooltip/tooltip.directive';
import { isHtmlEmpty, sanitizeEditorHtml } from './sanitize-html';

export interface SdMentionItem {
  id: string;
  label: string;
  description?: string | null;
  avatarUrl?: string | null;
}

export type SdMentionSource = (query: string) => Promise<SdMentionItem[]> | Observable<SdMentionItem[]>;
/** Uploads an image file and resolves to the URL to embed. */
export type SdImageUploader = (file: File) => Promise<string> | Observable<string>;

interface MentionState {
  items: SdMentionItem[];
  index: number;
  left: number;
  top: number;
  command: (item: { id: string; label: string }) => void;
}

type UrlPromptKind = 'link' | 'image' | 'video';

/**
 * TipTap rich-text editor (`[(value)]` / ngModel / formControl carry HTML).
 * ```html
 * <sd-rich-text-editor [(value)]="body" placeholder="Write a reply…" [mentionSource]="searchAgents"
 *   [uploadImage]="upload" (submitted)="send()" (mentionsChange)="mentionIds = $event" />
 * ```
 * Bold/italic/underline/strike, H1–H3, lists, quotes, links, inline code + code blocks, tables, images
 * (URL, upload, paste), YouTube embeds, @mentions from an async source, placeholder and shortcuts
 * (Mod-B/I/U, Mod-Shift-X strike, Mod-Alt-1..3 headings, Mod-Shift-7/8 lists, Mod-E code, Mod-Alt-C
 * code block, Mod-K link, Mod-Enter submit). Emits client-sanitised HTML ('' when empty).
 */
@Component({
  selector: 'sd-rich-text-editor',
  imports: [FormsModule, SdIcon, SdAvatar, SdDropdownMenu, SdTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SdRichTextEditor), multi: true }],
  template: `
    <div class="frame" [class.is-focused]="focused()" [class.is-disabled]="isDisabled()" [class.is-invalid]="invalid()">
      @if (toolbar() !== 'none') {
        <div class="toolbar" role="toolbar" [attr.aria-label]="'Formatting'" (keydown)="onToolbarKey($event)">
          <button type="button" class="tb" [attr.aria-pressed]="isActive('bold')" aria-label="Bold" [sdTooltip]="'Bold (' + mod + '+B)'" (click)="run('bold')"><sd-icon name="bold" [size]="15" /></button>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('italic')" aria-label="Italic" [sdTooltip]="'Italic (' + mod + '+I)'" (click)="run('italic')"><sd-icon name="italic" [size]="15" /></button>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('underline')" aria-label="Underline" [sdTooltip]="'Underline (' + mod + '+U)'" (click)="run('underline')"><sd-icon name="underline" [size]="15" /></button>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('strike')" aria-label="Strikethrough" [sdTooltip]="'Strikethrough (' + mod + '+Shift+X)'" (click)="run('strike')"><sd-icon name="strikethrough" [size]="15" /></button>
          @if (toolbar() === 'full') {
            <span class="sep" aria-hidden="true"></span>
            <button type="button" class="tb" [attr.aria-pressed]="isActive('heading', { level: 1 })" aria-label="Heading 1" (click)="run('h1')"><sd-icon name="heading-1" [size]="15" /></button>
            <button type="button" class="tb" [attr.aria-pressed]="isActive('heading', { level: 2 })" aria-label="Heading 2" (click)="run('h2')"><sd-icon name="heading-2" [size]="15" /></button>
            <button type="button" class="tb" [attr.aria-pressed]="isActive('heading', { level: 3 })" aria-label="Heading 3" (click)="run('h3')"><sd-icon name="heading-3" [size]="15" /></button>
          }
          <span class="sep" aria-hidden="true"></span>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('bulletList')" aria-label="Bulleted list" (click)="run('ul')"><sd-icon name="list" [size]="15" /></button>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('orderedList')" aria-label="Numbered list" (click)="run('ol')"><sd-icon name="list-ordered" [size]="15" /></button>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('blockquote')" aria-label="Quote" (click)="run('quote')"><sd-icon name="quote" [size]="15" /></button>
          <span class="sep" aria-hidden="true"></span>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('link')" aria-label="Link" [sdTooltip]="'Link (' + mod + '+K)'" (click)="openPrompt('link')"><sd-icon name="link" [size]="15" /></button>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('code')" aria-label="Inline code" [sdTooltip]="'Code (' + mod + '+E)'" (click)="run('code')"><sd-icon name="code" [size]="15" /></button>
          <button type="button" class="tb" [attr.aria-pressed]="isActive('codeBlock')" aria-label="Code block" (click)="run('codeBlock')"><sd-icon name="code-block" [size]="15" /></button>
          @if (toolbar() === 'full') {
            <button type="button" class="tb" aria-label="Insert image" (click)="openPrompt('image')"><sd-icon name="image" [size]="15" /></button>
            <button type="button" class="tb" aria-label="Embed YouTube video" (click)="openPrompt('video')"><sd-icon name="video" [size]="15" /></button>
            <sd-dropdown-menu class="tb-menu" icon="table" iconOnly variant="ghost" size="sm" ariaLabel="Table" [caret]="false" [items]="tableItems()" />
          }
          <span class="sd-spacer"></span>
          <button type="button" class="tb" aria-label="Undo" (click)="run('undo')"><sd-icon name="undo" [size]="15" /></button>
          <button type="button" class="tb" aria-label="Redo" (click)="run('redo')"><sd-icon name="redo" [size]="15" /></button>
        </div>
      }

      @if (prompt(); as p) {
        <form class="prompt" (submit)="applyPrompt($event)" (keydown.escape)="closePrompt()">
          <sd-icon [name]="p === 'link' ? 'link' : p === 'image' ? 'image' : 'video'" [size]="15" />
          <input
            #promptInput
            class="sd-input sd-input-sm"
            type="url"
            [attr.aria-label]="p === 'link' ? 'Link URL' : p === 'image' ? 'Image URL' : 'YouTube URL'"
            [placeholder]="p === 'video' ? 'https://www.youtube.com/watch?v=…' : 'https://…'"
            [(ngModel)]="promptValue"
            name="url"
          />
          @if (p === 'image' && uploadImage()) {
            <label class="upload">
              Upload…
              <input type="file" accept="image/*" class="sd-visually-hidden" (change)="onFilePicked($event)" />
            </label>
          }
          <button type="submit" class="prompt-btn primary">Apply</button>
          @if (p === 'link' && isActive('link')) {
            <button type="button" class="prompt-btn" (click)="removeLink()">Remove</button>
          }
          <button type="button" class="prompt-btn" (click)="closePrompt()">Cancel</button>
        </form>
      }

      <div
        #host
        class="content sd-prose"
        [style.min-height]="minHeight()"
        [style.max-height]="maxHeight()"
      ></div>
      @if (uploading()) {
        <p class="status" role="status">Uploading image…</p>
      }
    </div>

    @if (mention(); as m) {
      <div class="sd-popover mentions" role="listbox" aria-label="Mention suggestions" [style.left.px]="m.left" [style.top.px]="m.top">
        @for (item of m.items; track item.id; let i = $index) {
          <div
            class="mention-item"
            role="option"
            [attr.aria-selected]="i === m.index"
            [class.is-active]="i === m.index"
            (mousedown)="$event.preventDefault(); pickMention(item)"
          >
            <sd-avatar [name]="item.label" [src]="item.avatarUrl ?? null" size="xs" />
            <span class="sd-truncate">{{ item.label }}</span>
            @if (item.description) {
              <span class="sd-muted sd-xs sd-truncate">{{ item.description }}</span>
            }
          </div>
        } @empty {
          <div class="mention-empty">No matching people</div>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .frame {
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      transition: border-color var(--sd-dur-fast) var(--sd-ease), box-shadow var(--sd-dur-fast) var(--sd-ease);
    }
    .frame.is-focused {
      border-color: var(--sd-primary);
      box-shadow: 0 0 0 3px var(--sd-focus);
    }
    .frame.is-invalid {
      border-color: var(--sd-danger);
    }
    .frame.is-disabled {
      background: var(--sd-surface-2);
      opacity: 0.75;
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 2px;
      padding: 4px 6px;
      border-bottom: 1px solid var(--sd-divider);
    }
    .tb {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 28px;
      height: 28px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-2);
      cursor: pointer;
    }
    .tb:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .tb[aria-pressed='true'] {
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .sep {
      width: 1px;
      height: 18px;
      margin: 0 4px;
      background: var(--sd-border);
    }
    .prompt {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 6px 8px;
      border-bottom: 1px solid var(--sd-divider);
      background: var(--sd-surface-2);
      color: var(--sd-text-3);
    }
    .prompt .sd-input {
      flex: 1;
    }
    .prompt-btn,
    .upload {
      height: 28px;
      padding: 0 10px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface);
      color: var(--sd-text);
      font-size: var(--sd-text-sm);
      cursor: pointer;
      display: inline-flex;
      align-items: center;
    }
    .prompt-btn.primary {
      background: var(--sd-primary);
      border-color: transparent;
      color: var(--sd-on-primary);
    }
    .content {
      overflow: auto;
      padding: 10px 12px;
      cursor: text;
    }
    .content ::ng-deep .ProseMirror {
      min-height: inherit;
      outline: none;
      white-space: pre-wrap;
      word-wrap: break-word;
    }
    .content ::ng-deep .ProseMirror p.is-editor-empty:first-child::before {
      content: attr(data-placeholder);
      float: left;
      height: 0;
      color: var(--sd-text-3);
      pointer-events: none;
    }
    .content ::ng-deep .ProseMirror .selectedCell {
      background: var(--sd-selection);
    }
    .content ::ng-deep .ProseMirror table {
      table-layout: fixed;
    }
    .content ::ng-deep .ProseMirror img.ProseMirror-selectednode {
      outline: 2px solid var(--sd-primary);
    }
    .status {
      padding: 4px 12px 8px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .mentions {
      position: fixed;
      z-index: var(--sd-z-overlay);
      min-width: 220px;
      max-width: 320px;
      max-height: 260px;
      overflow: auto;
      padding: 4px;
    }
    .mention-item {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 8px;
      border-radius: var(--sd-radius-sm);
      cursor: pointer;
    }
    .mention-item.is-active {
      background: var(--sd-surface-3);
    }
    .mention-empty {
      padding: 8px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class SdRichTextEditor extends SdControlBase<string> {
  readonly value = model('');
  readonly placeholder = input('Write something…');
  readonly toolbar = input<'full' | 'compact' | 'none'>('full');
  readonly minHeight = input('120px');
  readonly maxHeight = input('420px');
  readonly ariaLabel = input('Message');
  readonly autofocus = input(false, { transform: booleanAttribute });
  readonly mentionSource = input<SdMentionSource | null>(null);
  readonly uploadImage = input<SdImageUploader | null>(null);

  /** Mod+Enter pressed. */
  readonly submitted = output<void>();
  /** Ids of everyone @mentioned in the current content. */
  readonly mentionsChange = output<string[]>();
  readonly focusChange = output<boolean>();

  protected readonly mod = /Mac|iPhone|iPad/i.test(globalThis.navigator?.platform ?? '') ? '⌘' : 'Ctrl';
  protected readonly focused = signal(false);
  protected readonly mention = signal<MentionState | null>(null);
  protected readonly prompt = signal<UrlPromptKind | null>(null);
  protected readonly uploading = signal(false);
  protected promptValue = '';
  private readonly revision = signal(0);
  private readonly hostEl = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly promptInput = viewChild<ElementRef<HTMLInputElement>>('promptInput');
  private editor: Editor | null = null;
  private lastEmitted = '';
  private lastMentions = '';

  protected readonly tableItems = computed<SdMenuItem[]>(() => {
    this.revision();
    const inTable = this.editor?.isActive('table') ?? false;
    return [
      { id: 'insert', label: 'Insert table', icon: 'table', run: () => this.chain()?.insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run() },
      { id: 'd1', divider: true },
      { id: 'row', label: 'Add row below', disabled: !inTable, run: () => this.chain()?.addRowAfter().run() },
      { id: 'col', label: 'Add column right', disabled: !inTable, run: () => this.chain()?.addColumnAfter().run() },
      { id: 'delrow', label: 'Delete row', disabled: !inTable, run: () => this.chain()?.deleteRow().run() },
      { id: 'delcol', label: 'Delete column', disabled: !inTable, run: () => this.chain()?.deleteColumn().run() },
      { id: 'd2', divider: true },
      { id: 'deltable', label: 'Delete table', danger: true, disabled: !inTable, run: () => this.chain()?.deleteTable().run() },
    ];
  });

  constructor() {
    super();
    afterNextRender(() => this.createEditor());
    effect(() => {
      const html = this.value() ?? '';
      untracked(() => {
        if (!this.editor || html === this.lastEmitted) return;
        this.lastEmitted = html;
        this.editor.commands.setContent(html || '', { emitUpdate: false });
      });
    });
    effect(() => {
      const editable = !this.isDisabled();
      untracked(() => this.editor?.setEditable(editable));
    });
    inject(DestroyRef).onDestroy(() => {
      this.editor?.destroy();
      this.editor = null;
    });
  }

  protected emptyValue(): string {
    return '';
  }

  /** Focus the editor (e.g. after clicking "Reply"). */
  focus(): void {
    this.editor?.commands.focus('end');
  }

  /** Inserts HTML at the cursor (templates, canned responses, AI drafts). */
  insertHtml(html: string): void {
    this.editor?.chain().focus().insertContent(html).run();
  }

  clear(): void {
    this.editor?.commands.clearContent(true);
  }

  protected isActive(name: string, attrs?: Record<string, unknown>): boolean {
    this.revision();
    return this.editor?.isActive(name, attrs) ?? false;
  }

  protected run(action: string): void {
    const c = this.chain();
    if (!c) return;
    switch (action) {
      case 'bold':
        c.toggleBold().run();
        break;
      case 'italic':
        c.toggleItalic().run();
        break;
      case 'underline':
        c.toggleUnderline().run();
        break;
      case 'strike':
        c.toggleStrike().run();
        break;
      case 'h1':
        c.toggleHeading({ level: 1 }).run();
        break;
      case 'h2':
        c.toggleHeading({ level: 2 }).run();
        break;
      case 'h3':
        c.toggleHeading({ level: 3 }).run();
        break;
      case 'ul':
        c.toggleBulletList().run();
        break;
      case 'ol':
        c.toggleOrderedList().run();
        break;
      case 'quote':
        c.toggleBlockquote().run();
        break;
      case 'code':
        c.toggleCode().run();
        break;
      case 'codeBlock':
        c.toggleCodeBlock().run();
        break;
      case 'undo':
        c.undo().run();
        break;
      case 'redo':
        c.redo().run();
        break;
    }
  }

  protected openPrompt(kind: UrlPromptKind): void {
    this.promptValue = kind === 'link' ? ((this.editor?.getAttributes('link')['href'] as string | undefined) ?? '') : '';
    this.prompt.set(kind);
    queueMicrotask(() => this.promptInput()?.nativeElement.focus());
  }

  protected closePrompt(): void {
    this.prompt.set(null);
    this.editor?.commands.focus();
  }

  protected applyPrompt(event: Event): void {
    event.preventDefault();
    const url = this.promptValue.trim();
    const kind = this.prompt();
    this.prompt.set(null);
    const c = this.chain();
    if (!c || !kind) return;
    if (!url) {
      if (kind === 'link') c.extendMarkRange('link').unsetLink().run();
      return;
    }
    if (!/^(https?:|mailto:|tel:|\/)/i.test(url)) return;
    if (kind === 'link') c.extendMarkRange('link').setLink({ href: url }).run();
    else if (kind === 'image') c.setImage({ src: url }).run();
    else c.setYoutubeVideo({ src: url }).run();
  }

  protected removeLink(): void {
    this.prompt.set(null);
    this.chain()?.extendMarkRange('link').unsetLink().run();
  }

  protected onFilePicked(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    this.prompt.set(null);
    if (file) void this.uploadAndInsert(file);
  }

  protected pickMention(item: SdMentionItem): void {
    this.mention()?.command({ id: item.id, label: item.label });
  }

  protected onToolbarKey(event: KeyboardEvent): void {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return;
    const buttons = Array.from((event.currentTarget as HTMLElement).querySelectorAll<HTMLButtonElement>('button'));
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (index < 0) return;
    event.preventDefault();
    const next = (index + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length;
    buttons[next].focus();
  }

  private chain() {
    return this.editor?.chain().focus();
  }

  private async uploadAndInsert(file: File): Promise<void> {
    const uploader = this.uploadImage();
    if (!uploader || !file.type.startsWith('image/')) return;
    this.uploading.set(true);
    try {
      const result = uploader(file);
      const url = isObservable(result) ? await firstValueFrom(result) : await result;
      if (url) this.chain()?.setImage({ src: url, alt: file.name }).run();
    } catch {
      /* the uploader reports its own errors */
    } finally {
      this.uploading.set(false);
    }
  }

  private createEditor(): void {
    const self = this;
    const shortcuts = Extension.create({
      name: 'sdShortcuts',
      addKeyboardShortcuts() {
        return {
          'Mod-Enter': () => {
            self.submitted.emit();
            return true;
          },
          'Mod-k': () => {
            self.openPrompt('link');
            return true;
          },
        };
      },
    });

    this.editor = new Editor({
      element: this.hostEl().nativeElement,
      content: this.value() || '',
      editable: !this.isDisabled(),
      autofocus: this.autofocus() ? 'end' : false,
      extensions: [
        StarterKit.configure({ codeBlock: false, link: false }),
        CodeBlock,
        Link.configure({ openOnClick: false, autolink: true, protocols: ['http', 'https', 'mailto', 'tel'] }),
        Image.configure({ inline: false, allowBase64: false }),
        Youtube.configure({ nocookie: true, controls: true, width: 560, height: 315 }),
        Table.configure({ resizable: false }),
        TableRow,
        TableHeader,
        TableCell,
        Placeholder.configure({ placeholder: () => this.placeholder() }),
        Mention.configure({
          HTMLAttributes: { class: 'mention' },
          suggestion: {
            char: '@',
            items: async ({ query }: { query: string }) => this.fetchMentions(query),
            render: () => ({
              onStart: (props) => this.showMentions(props.items as SdMentionItem[], props.clientRect?.() ?? null, props.command),
              onUpdate: (props) => this.showMentions(props.items as SdMentionItem[], props.clientRect?.() ?? null, props.command),
              onKeyDown: ({ event }) => this.onMentionKey(event),
              onExit: () => this.mention.set(null),
            }),
          },
        }),
        shortcuts,
      ],
      editorProps: {
        attributes: {
          role: 'textbox',
          'aria-multiline': 'true',
          'aria-label': this.ariaLabel(),
          ...(this.describedBy() ? { 'aria-describedby': this.describedBy()! } : {}),
          id: this.controlId(),
        },
        handlePaste: (_view, event) => {
          const file = Array.from(event.clipboardData?.files ?? []).find((f) => f.type.startsWith('image/'));
          if (file && this.uploadImage()) {
            void this.uploadAndInsert(file);
            return true;
          }
          return false;
        },
        handleDrop: (_view, event) => {
          const file = Array.from((event as DragEvent).dataTransfer?.files ?? []).find((f) => f.type.startsWith('image/'));
          if (file && this.uploadImage()) {
            event.preventDefault();
            void this.uploadAndInsert(file);
            return true;
          }
          return false;
        },
      },
      onUpdate: ({ editor }) => this.emitChange(editor),
      onTransaction: () => this.revision.update((n) => n + 1),
      onFocus: () => {
        this.focused.set(true);
        this.focusChange.emit(true);
      },
      onBlur: () => {
        this.focused.set(false);
        this.focusChange.emit(false);
        this.touched();
      },
    });
    this.lastEmitted = this.value() || '';
  }

  private emitChange(editor: Editor): void {
    const raw = editor.getHTML();
    const html = isHtmlEmpty(raw) ? '' : sanitizeEditorHtml(raw);
    this.lastEmitted = html;
    this.commit(html);
    const ids: string[] = [];
    editor.state.doc.descendants((node) => {
      if (node.type.name === 'mention' && node.attrs['id']) ids.push(String(node.attrs['id']));
    });
    const key = ids.join(',');
    if (key !== this.lastMentions) {
      this.lastMentions = key;
      this.mentionsChange.emit([...new Set(ids)]);
    }
  }

  private async fetchMentions(query: string): Promise<SdMentionItem[]> {
    const source = this.mentionSource();
    if (!source) return [];
    try {
      const result = source(query);
      const items = isObservable(result) ? await firstValueFrom(result) : await result;
      return items.slice(0, 8);
    } catch {
      return [];
    }
  }

  private showMentions(items: SdMentionItem[], rect: DOMRect | null, command: (item: { id: string; label: string }) => void): void {
    if (!this.mentionSource()) return;
    const previous = this.mention();
    this.mention.set({
      items,
      index: previous ? Math.min(previous.index, Math.max(0, items.length - 1)) : 0,
      left: rect?.left ?? 0,
      top: (rect?.bottom ?? 0) + 4,
      command,
    });
  }

  private onMentionKey(event: KeyboardEvent): boolean {
    const m = this.mention();
    if (!m) return false;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const n = m.items.length || 1;
      this.mention.set({ ...m, index: (m.index + (event.key === 'ArrowDown' ? 1 : -1) + n) % n });
      return true;
    }
    if (event.key === 'Enter' || event.key === 'Tab') {
      const item = m.items[m.index];
      if (item) m.command({ id: item.id, label: item.label });
      return true;
    }
    if (event.key === 'Escape') {
      this.mention.set(null);
      return true;
    }
    return false;
  }
}

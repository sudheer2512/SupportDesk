import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, CommunityApi, PostDto, TopicDetailDto } from '@sd/core';
import { SdButton, SdChipsInput, SdDialog, SdFormField, SdIcon, SdInput, SdRichTextEditor, isHtmlEmpty } from '@sd/ui';
import { firstValueFrom } from 'rxjs';

/** Edit a community topic's title, body and tags (moderator / author). Closes with the updated topic. */
@Component({
  selector: 'desk-kb-topic-edit-dialog',
  imports: [FormsModule, SdDialog, SdButton, SdFormField, SdInput, SdRichTextEditor, SdChipsInput, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Edit topic" description="Changes are visible to everyone in the community.">
      <div class="form">
        <sd-form-field label="Title" required [error]="errors()['title']">
          <input sdInput maxlength="200" [ngModel]="title()" (ngModelChange)="title.set($event)" />
        </sd-form-field>
        <sd-form-field label="Body" required [error]="errors()['bodyHtml']">
          <sd-rich-text-editor ariaLabel="Topic body" toolbar="compact" minHeight="180px" [(value)]="body" />
        </sd-form-field>
        <sd-form-field label="Tags" optional [error]="errors()['tags']">
          <sd-chips-input placeholder="Add tag" [lowercase]="true" [(value)]="tags" />
        </sd-form-field>
        @if (formError(); as err) {
          <p class="form-error" role="alert"><sd-icon name="circle-alert" [size]="14" /> {{ err }}</p>
        }
      </div>
      <div sdDialogFooter>
        <button sdButton type="button" [disabled]="saving()" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="button" [loading]="saving()" (click)="save()">Save changes</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .form-error {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class TopicEditDialog {
  private readonly topic = inject<TopicDetailDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<TopicDetailDto>>(DialogRef);
  private readonly api = inject(CommunityApi);

  protected readonly title = signal(this.topic.title);
  protected readonly body = signal(this.topic.bodyHtml);
  protected readonly tags = signal<string[]>([...this.topic.tags]);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected async save(): Promise<void> {
    const errors = {
      title: this.title().trim() ? null : 'Enter a title.',
      bodyHtml: isHtmlEmpty(this.body()) ? 'The topic needs a body.' : null,
    };
    this.errors.set(errors);
    if (errors.title || errors.bodyHtml) return;
    this.saving.set(true);
    this.formError.set(null);
    try {
      const updated = await firstValueFrom(this.api.updateTopic(this.topic.id, { title: this.title().trim(), bodyHtml: this.body(), tags: this.tags() }));
      this.ref.close(updated);
    } catch (err) {
      const e = ApiError.from(err);
      const fields = { title: e.fieldError('title'), bodyHtml: e.fieldError('bodyHtml'), tags: e.fieldError('tags') };
      this.errors.set(fields);
      if (!fields.title && !fields.bodyHtml && !fields.tags) this.formError.set(e.userMessage);
    } finally {
      this.saving.set(false);
    }
  }
}

/** Edit the body of one community post. Closes with the updated post. */
@Component({
  selector: 'desk-kb-post-edit-dialog',
  imports: [SdDialog, SdButton, SdFormField, SdRichTextEditor, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog title="Edit reply" [description]="'Posted by ' + post.authorName">
      <sd-form-field label="Reply" required [error]="error()">
        <sd-rich-text-editor ariaLabel="Reply body" toolbar="compact" minHeight="180px" [(value)]="body" />
      </sd-form-field>
      @if (formError(); as err) {
        <p class="form-error" role="alert"><sd-icon name="circle-alert" [size]="14" /> {{ err }}</p>
      }
      <div sdDialogFooter>
        <button sdButton type="button" [disabled]="saving()" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="button" [loading]="saving()" (click)="save()">Save reply</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .form-error {
      display: flex;
      align-items: center;
      gap: 6px;
      margin-top: var(--sd-space-2);
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class PostEditDialog {
  protected readonly post = inject<PostDto>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<PostDto>>(DialogRef);
  private readonly api = inject(CommunityApi);

  protected readonly body = signal(this.post.bodyHtml);
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected async save(): Promise<void> {
    if (isHtmlEmpty(this.body())) {
      this.error.set('The reply cannot be empty.');
      return;
    }
    this.error.set(null);
    this.formError.set(null);
    this.saving.set(true);
    try {
      const updated = await firstValueFrom(this.api.updatePost(this.post.id, { bodyHtml: this.body() }));
      this.ref.close(updated);
    } catch (err) {
      const e = ApiError.from(err);
      const field = e.fieldError('bodyHtml');
      this.error.set(field);
      if (!field) this.formError.set(e.userMessage);
    } finally {
      this.saving.set(false);
    }
  }
}

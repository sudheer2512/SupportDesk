import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MeStore, OrganisationApi, SkillDto, saveErrorMessage, validationMessages } from '@sd/core';
import { SdButton, SdDialog, SdDialogService, SdEmptyState, SdFormField, SdInput, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { ModuleTabsComponent } from '../customers/shared/module-tabs.component';
import { SECTION_STYLES, TABLE_STYLES } from '../customers/shared/styles';
import { FORM_STYLES, PAGE_STYLES, TEAMS_TABS, errorText } from './shared/org-ui';

/** /teams/skills — skills catalogue used by skill-based routing (agents hold skills with a 1–5 proficiency). */
@Component({
  selector: 'desk-skills-page',
  imports: [FormsModule, SdPageHeader, SdButton, SdSkeleton, SdEmptyState, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Teams & Agents" subtitle="Skills catalogue for skill-based routing." icon="users">
        @if (canManage) {
          <button sdButton sdPageActions variant="primary" icon="plus" (click)="edit(null)">New skill</button>
        }
      </sd-page-header>
      <desk-module-tabs [tabs]="tabs" ariaLabel="Teams & Agents sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="sd-visually-hidden" for="skill-q">Filter skills</label>
          <input id="skill-q" class="sd-input" type="search" placeholder="Filter skills" [ngModel]="q()" (ngModelChange)="q.set($event)" />
        </div>
        @if (error()) {
          <div class="error-box" role="alert"><span>{{ error() }}</span><button sdButton size="sm" (click)="load()">Try again</button></div>
        } @else if (loading()) {
          <div aria-busy="true"><sd-skeleton [lines]="6" /></div>
        } @else if (filtered().length === 0) {
          <sd-empty-state icon="award" title="No skills" message="Add skills such as products, modules or languages, then give agents a proficiency level." />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <caption class="sd-visually-hidden">Skills</caption>
              <thead>
                <tr>
                  <th scope="col">Skill</th>
                  <th scope="col">Category</th>
                  <th scope="col">Description</th>
                  <th scope="col" class="num">Agents</th>
                  <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (s of filtered(); track s.id) {
                  <tr>
                    <td><strong>{{ s.name }}</strong></td>
                    <td>{{ s.category ?? '—' }}</td>
                    <td class="small">{{ s.description ?? '' }}</td>
                    <td class="num">{{ s.agentCount }}</td>
                    <td class="row-actions">
                      @if (canManage) {
                        <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + s.name" (click)="edit(s)"></button>
                        <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + s.name" (click)="remove(s)"></button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>
  `,
  styles: [TABLE_STYLES, SECTION_STYLES, PAGE_STYLES],
})
export class SkillsPage {
  private readonly api = inject(OrganisationApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canManage = inject(MeStore).canAny(['admin.organisation', 'agents.manage']);
  protected readonly tabs = TEAMS_TABS;
  protected readonly skills = signal<SkillDto[]>([]);
  protected readonly q = signal('');
  protected readonly filtered = computed(() => {
    const q = this.q().trim().toLowerCase();
    return q ? this.skills().filter((s) => `${s.name} ${s.category ?? ''}`.toLowerCase().includes(q)) : this.skills();
  });
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.skills().subscribe({
      next: (s) => {
        this.skills.set(s);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(errorText(err));
        this.loading.set(false);
      },
    });
  }

  protected edit(skill: SkillDto | null): void {
    this.dialogs.open<SkillDto, SkillDto | null>(SkillFormDialog, { data: skill }).closed.subscribe((saved) => {
      if (!saved) return;
      this.toast.success(skill ? 'Skill updated' : 'Skill added', saved.name);
      this.load();
    });
  }

  protected async remove(skill: SkillDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: `Delete ${skill.name}?`,
      message: skill.agentCount > 0 ? `${skill.agentCount} agents lose this skill.` : undefined,
      danger: true,
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    this.api.deleteSkill(skill.id).subscribe(() => {
      this.toast.success('Skill deleted', skill.name);
      this.load();
    });
  }
}

/** Create / edit a skill. */
@Component({
  selector: 'desk-skill-form-dialog',
  imports: [FormsModule, SdDialog, SdFormField, SdInput, SdButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="s ? 'Edit ' + s.name : 'New skill'">
      <form id="skill-form" class="form" (ngSubmit)="save()" novalidate>
        <div class="two">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [(ngModel)]="name" required maxlength="80" />
          </sd-form-field>
          <sd-form-field label="Category" optional hint="e.g. Product, Language, Soft skills">
            <input sdInput name="category" [(ngModel)]="category" maxlength="80" />
          </sd-form-field>
        </div>
        <sd-form-field label="Description" optional>
          <textarea sdInput name="description" rows="3" [(ngModel)]="description" maxlength="1000"></textarea>
        </sd-form-field>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="skill-form" [loading]="saving()">Save</button>
      </div>
    </sd-dialog>
  `,
  styles: FORM_STYLES,
})
export class SkillFormDialog {
  protected readonly s = inject<SkillDto | null>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<SkillDto>>(DialogRef);
  private readonly api = inject(OrganisationApi);
  protected name = this.s?.name ?? '';
  protected category = this.s?.category ?? '';
  protected description = this.s?.description ?? '';
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);

  protected save(): void {
    this.saving.set(true);
    this.api.saveSkill(this.s?.id ?? null, { name: this.name.trim(), category: this.category.trim() || null, description: this.description.trim() || null }).subscribe({
      next: (r) => this.ref.close(r),
      error: (err: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(err));
        this.formError.set(saveErrorMessage(err));
      },
    });
  }
}

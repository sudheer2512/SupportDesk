import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AnalyticsApi, AnalyticsDashboardDto, saveErrorMessage, validationMessages } from '@sd/core';
import { SdButton, SdDialog, SdFormField, SdInput, SdToggle } from '@sd/ui';
import { AnalyticsStore } from './analytics.store';

export interface DashboardFormData {
  /** Edit settings of this dashboard (widgets are kept); null = new empty dashboard. */
  dashboard: AnalyticsDashboardDto | null;
}

/** Create a dashboard or change its name, description, default period and sharing. */
@Component({
  selector: 'desk-analytics-dashboard-form',
  imports: [FormsModule, SdButton, SdDialog, SdFormField, SdInput, SdToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="d ? 'Dashboard settings' : 'New dashboard'" description="Personal dashboards are visible to you only; shared ones to everyone with analytics access.">
      <form id="dashboard-form" class="form" (ngSubmit)="save()" novalidate>
        <sd-form-field label="Name" required [error]="errors()['name']">
          <input sdInput name="name" [(ngModel)]="name" maxlength="120" required />
        </sd-form-field>
        <sd-form-field label="Description" optional [error]="errors()['description']">
          <textarea sdInput name="description" rows="2" [(ngModel)]="description" maxlength="500"></textarea>
        </sd-form-field>
        <sd-form-field label="Default period" [error]="errors()['period']">
          <select sdInput name="period" [(ngModel)]="period">
            @for (p of periods(); track p.key) {
              <option [value]="p.key">{{ p.label }}</option>
            }
          </select>
        </sd-form-field>
        @if (store.teamScope() && !d?.isDefault) {
          <sd-toggle [(value)]="shared">Share with everyone who has analytics access</sd-toggle>
        }
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDialogFooter>
        <button sdButton type="button" (click)="ref.close()">Cancel</button>
        <button sdButton variant="primary" type="submit" form="dashboard-form" [loading]="saving()">{{ d ? 'Save' : 'Create dashboard' }}</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      max-height: calc(100dvh - 48px);
    }
    .form {
      display: grid;
      gap: var(--sd-space-3);
    }
    .error {
      color: var(--sd-danger);
    }
  `,
})
export class DashboardFormDialog {
  protected readonly data = inject<DashboardFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AnalyticsDashboardDto>>(DialogRef);
  protected readonly store = inject(AnalyticsStore);
  private readonly api = inject(AnalyticsApi);

  protected readonly d = this.data.dashboard;
  protected name = this.d?.name ?? '';
  protected description = this.d?.description ?? '';
  protected period = this.d?.period ?? 'last_30_days';
  protected readonly shared = signal(this.d?.isShared ?? false);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly periods = computed(() => this.store.periods().filter((p) => p.key !== 'custom'));

  protected save(): void {
    if (this.saving()) return;
    if (!this.name.trim()) {
      this.errors.set({ name: 'Enter a name.' });
      return;
    }
    const request = {
      name: this.name.trim(),
      description: this.description.trim() || null,
      period: this.period,
      isShared: this.d?.isDefault ? true : this.shared(),
      widgets: this.d?.widgets ?? [],
      rowVersion: this.d?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const call = this.d ? this.api.updateDashboard(this.d.id, request) : this.api.createDashboard(request);
    call.subscribe({
      next: (saved) => this.ref.close(saved),
      error: (e: unknown) => {
        this.saving.set(false);
        this.errors.set(validationMessages(e));
        this.formError.set(saveErrorMessage(e));
      },
    });
  }
}

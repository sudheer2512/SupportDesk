import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ContactMergeResultDto, ContactsApi, DuplicatePairDto, MeStore } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdEmptyState, SdInput, SdPageHeader, SdSkeleton, ToastService } from '@sd/ui';
import { MergeWizardData, MergeWizardDialog } from './merge-wizard.dialog';
import { CUSTOMER_TABS, ModuleTabsComponent } from './shared/module-tabs.component';
import { TABLE_STYLES } from './shared/styles';

/** /customers/duplicates — tenant-wide duplicate contact finder (exact e-mail/phone matches and fuzzy names within a customer). */
@Component({
  selector: 'desk-duplicates-page',
  imports: [FormsModule, RouterLink, SdPageHeader, SdButton, SdBadge, SdInput, SdSkeleton, SdEmptyState, ModuleTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Duplicate contacts" subtitle="Same e-mail or phone anywhere, or near-identical names within a customer." icon="merge" />
      <desk-module-tabs [tabs]="tabs" ariaLabel="Customers sections" />
      <div class="desk-page__body">
        <div class="toolbar">
          <label class="sd-small" for="min-score">Minimum match</label>
          <select sdInput size="sm" id="min-score" class="f" [ngModel]="minScore()" (ngModelChange)="minScore.set(+$event); load()">
            <option [ngValue]="0.4">40% (all candidates)</option>
            <option [ngValue]="0.6">60% (medium and high)</option>
            <option [ngValue]="0.85">85% (high only)</option>
          </select>
          <button sdButton size="sm" icon="refresh" (click)="load()">Rescan</button>
        </div>
        @if (loading()) {
          <sd-skeleton [lines]="6" />
        } @else if (pairs().length === 0) {
          <sd-empty-state icon="check-check" title="No duplicates found" message="Every contact looks unique at this threshold." />
        } @else {
          <div class="dt-wrap">
            <table class="dt">
              <thead>
                <tr>
                  <th scope="col">Contact A</th>
                  <th scope="col">Contact B</th>
                  <th scope="col">Why</th>
                  <th scope="col">Match</th>
                  <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (p of pairs(); track p.first.id + p.second.id) {
                  <tr>
                    <td>
                      <a [routerLink]="['/customers/contacts', p.first.id]">{{ p.first.fullName }}</a>
                      <div class="sd-xs sd-muted">{{ p.first.email ?? p.first.phone }} · {{ p.first.customerName ?? 'No customer' }}</div>
                    </td>
                    <td>
                      <a [routerLink]="['/customers/contacts', p.second.id]">{{ p.second.fullName }}</a>
                      <div class="sd-xs sd-muted">{{ p.second.email ?? p.second.phone }} · {{ p.second.customerName ?? 'No customer' }}</div>
                    </td>
                    <td class="sd-small">{{ p.reasons.join(' · ') }}</td>
                    <td>
                      <sd-badge [tone]="p.confidence === 'High' ? 'danger' : p.confidence === 'Medium' ? 'warning' : 'neutral'" size="sm">{{ (p.score * 100).toFixed(0) }}% · {{ p.confidence }}</sd-badge>
                    </td>
                    <td class="row-actions">
                      @if (canMerge) {
                        <button sdButton size="sm" icon="merge" (click)="merge(p)">Review &amp; merge</button>
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
  styles: [
    TABLE_STYLES,
    `
      .toolbar {
        display: flex;
        align-items: center;
        gap: var(--sd-space-2);
        margin-bottom: var(--sd-space-3);
      }
      .f {
        width: 220px;
      }
      .row-actions {
        text-align: right;
      }
    `,
  ],
})
export class DuplicatesPage {
  private readonly api = inject(ContactsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  protected readonly canMerge = inject(MeStore).can('contacts.merge');
  protected readonly tabs = CUSTOMER_TABS;
  protected readonly pairs = signal<DuplicatePairDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly minScore = signal(0.4);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.api.duplicates(null, this.minScore(), 100).subscribe({
      next: (p) => {
        this.pairs.set(p);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected merge(p: DuplicatePairDto): void {
    this.dialog
      .open<ContactMergeResultDto, MergeWizardData>(MergeWizardDialog, { data: { firstId: p.first.id, secondId: p.second.id, score: p.score, reasons: p.reasons }, size: 'lg' })
      .closed.subscribe((r) => {
        if (!r) return;
        this.toast.success('Contacts merged', `${r.tickets} tickets moved`);
        this.load();
      });
  }
}

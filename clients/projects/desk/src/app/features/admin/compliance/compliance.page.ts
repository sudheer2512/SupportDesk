import { ChangeDetectionStrategy, Component, OnInit, input, signal } from '@angular/core';
import { SdPageHeader, SdTab, SdTabs } from '@sd/ui';
import { HoldsTabComponent } from './holds-tab.component';
import { RequestsTabComponent } from './requests-tab.component';
import { RetentionTabComponent } from './retention-tab.component';
import { SupportAccessTabComponent } from './support-access-tab.component';

/** Compliance (admin.compliance): retention policies, legal holds, data-subject requests and platform support access. */
@Component({
  selector: 'desk-compliance-page',
  imports: [SdPageHeader, SdTabs, SdTab, RetentionTabComponent, HoldsTabComponent, RequestsTabComponent, SupportAccessTabComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Compliance" icon="scale" subtitle="Retention, legal holds, data-subject rights and support access." [breadcrumbs]="crumbs" />
      <div class="desk-page__body">
        <sd-tabs [(selected)]="tab" ariaLabel="Compliance areas">
          <sd-tab id="retention" label="Retention" icon="archive">
            @if (tab() === 'retention') {
              <desk-retention-tab />
            }
          </sd-tab>
          <sd-tab id="holds" label="Legal holds" icon="scale">
            @if (tab() === 'holds') {
              <desk-holds-tab />
            }
          </sd-tab>
          <sd-tab id="requests" label="Data requests" icon="user-check">
            @if (tab() === 'requests') {
              <desk-requests-tab />
            }
          </sd-tab>
          <sd-tab id="support" label="Support access" icon="life-buoy">
            @if (tab() === 'support') {
              <desk-support-access-tab />
            }
          </sd-tab>
        </sd-tabs>
      </div>
    </div>
  `,
  styles: `
    sd-tabs { display: block; }
    :host ::ng-deep [role='tabpanel'] { padding-top: var(--sd-space-4); }
  `,
})
export class CompliancePage implements OnInit {
  /** ?tab= deep link. */
  readonly initialTab = input<string | undefined>(undefined, { alias: 'tab' });
  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Compliance' }];
  protected readonly tab = signal<string | null>('retention');

  ngOnInit(): void {
    const t = this.initialTab();
    if (t && ['retention', 'holds', 'requests', 'support'].includes(t)) this.tab.set(t);
  }
}

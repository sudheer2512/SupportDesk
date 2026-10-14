import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Routes } from '@angular/router';
import { PortalPlaceholderComponent } from '../../shared/portal-placeholder.component';

/** Submit a ticket — placeholder until the brand-specific ticket form is built. */
@Component({
  selector: 'portal-submit-home',
  imports: [PortalPlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<portal-placeholder title="Submit a Ticket" icon="file-plus" subtitle="Tell us what you need help with." [planned]="planned" />`,
})
export class SubmitHomePage {
  protected readonly planned = [
    'Form with the departments and fields this portal exposes',
    'Suggested articles while you type',
    'Attachments and product / installation selection',
  ];
}

export const ROUTES: Routes = [{ path: '', title: 'Submit a Ticket', component: SubmitHomePage }];

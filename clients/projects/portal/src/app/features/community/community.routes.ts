import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Routes } from '@angular/router';
import { PortalPlaceholderComponent } from '../../shared/portal-placeholder.component';

/** Community landing — placeholder until forums are built. */
@Component({
  selector: 'portal-community-home',
  imports: [PortalPlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<portal-placeholder title="Community" icon="messages" subtitle="Ask other customers and our experts." [planned]="planned" />`,
})
export class CommunityHomePage {
  protected readonly planned = ['Topics by product and category', 'Questions, ideas and announcements', 'Accepted answers from moderators'];
}

export const ROUTES: Routes = [{ path: '', title: 'Community', component: CommunityHomePage }];

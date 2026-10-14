import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Routes } from '@angular/router';
import { PortalPlaceholderComponent } from '../../shared/portal-placeholder.component';

/** Knowledge base landing — placeholder until the article browser is built. */
@Component({
  selector: 'portal-kb-home',
  imports: [PortalPlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<portal-placeholder title="Knowledge Base" icon="book-open" subtitle="Answers and how-to guides." [planned]="planned" />`,
})
export class KbHomePage {
  protected readonly planned = ['Search across published articles', 'Browse by category', 'Rate articles and suggest improvements'];
}

export const ROUTES: Routes = [{ path: '', title: 'Knowledge Base', component: KbHomePage }];

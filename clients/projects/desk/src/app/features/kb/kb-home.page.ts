import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Knowledge Base landing page — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-kb-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<desk-module-placeholder title="Knowledge Base" icon="book-open" [subtitle]="subtitle" [planned]="planned" />`,
})
export class KbHomePage {
  protected readonly subtitle = 'Articles, categories, review workflow and portal publishing.';
  protected readonly planned = [
    'Articles with categories, tags and versions',
    'Authoring, review and publishing workflow',
    'Internal vs public visibility per brand portal',
    'Article suggestions inside the ticket workbench',
  ];
}

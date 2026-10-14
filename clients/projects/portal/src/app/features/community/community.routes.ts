import { Routes } from '@angular/router';
import { portalAuthGuard } from '@sd/core';

/** Community forum: topic list, new topic (signed-in) and topic pages. */
export const ROUTES: Routes = [
  { path: '', title: 'Community', loadComponent: () => import('./community-list.page').then((m) => m.CommunityListPage) },
  {
    path: 'new',
    title: 'New topic',
    canActivate: [portalAuthGuard],
    loadComponent: () => import('./new-topic.page').then((m) => m.NewTopicPage),
  },
  { path: ':id', title: 'Community', loadComponent: () => import('./topic.page').then((m) => m.TopicPage) },
];

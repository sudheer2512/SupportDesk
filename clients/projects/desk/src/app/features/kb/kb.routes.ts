import { inject } from '@angular/core';
import { CanActivateFn, CanDeactivateFn, Router, Routes } from '@angular/router';
import { AuthService, MeStore, SD_CONFIG, permissionGuard } from '@sd/core';
import { KB_BASE, KbLeaveGuarded } from './kb-common';
import { KB_SECTION_PERMISSIONS, KbLayoutComponent } from './kb-layout.component';

const confirmLeave: CanDeactivateFn<KbLeaveGuarded> = (component) => component?.canDeactivate?.() ?? true;

/** /kb lands on the article list; community-only moderators are sent to the moderation queue instead. */
const articlesLanding: CanActivateFn = async (_route, state) => {
  const me = inject(MeStore);
  const router = inject(Router);
  const config = inject(SD_CONFIG);
  await inject(AuthService).ready();
  if (me.canAny(KB_SECTION_PERMISSIONS.articles)) return true;
  if (me.canAny(KB_SECTION_PERMISSIONS.community)) return router.parseUrl(`${KB_BASE}/community`);
  return router.createUrlTree([config.forbiddenPath], { queryParams: { from: state.url } });
};

/**
 * Knowledge Base module routes (lazy-loaded from app.routes.ts at /kb, permission-guarded there).
 * Every page is its own lazy chunk (the editor pulls in TipTap, analytics pulls in ECharts).
 */
export const ROUTES: Routes = [
  {
    path: '',
    component: KbLayoutComponent,
    children: [
      {
        path: '',
        pathMatch: 'full',
        title: 'Knowledge Base',
        canActivate: [articlesLanding],
        loadComponent: () => import('./articles/articles-list.page').then((m) => m.ArticlesListPage),
      },
      {
        path: 'articles/new',
        title: 'New article',
        canActivate: [permissionGuard('kb.author')],
        canDeactivate: [confirmLeave],
        loadComponent: () => import('./articles/article-editor.page').then((m) => m.ArticleEditorPage),
      },
      {
        path: 'articles/:id',
        title: 'Article',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.articles)],
        canDeactivate: [confirmLeave],
        loadComponent: () => import('./articles/article-editor.page').then((m) => m.ArticleEditorPage),
      },
      { path: 'articles', pathMatch: 'full', redirectTo: '' },
      {
        path: 'categories',
        title: 'Knowledge Base categories',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.categories)],
        loadComponent: () => import('./categories/categories.page').then((m) => m.CategoriesPage),
      },
      {
        path: 'feedback',
        title: 'Article feedback',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.feedback)],
        loadComponent: () => import('./feedback/feedback-queue.page').then((m) => m.FeedbackQueuePage),
      },
      {
        path: 'community',
        title: 'Community moderation',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.community)],
        loadComponent: () => import('./community/topics.page').then((m) => m.CommunityTopicsPage),
      },
      {
        path: 'community/:id',
        title: 'Community topic',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.community)],
        loadComponent: () => import('./community/topic-detail.page').then((m) => m.TopicDetailPage),
      },
      {
        path: 'flows',
        title: 'Guided troubleshooting',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.flows)],
        loadComponent: () => import('./flows/flows.page').then((m) => m.FlowsPage),
      },
      {
        path: 'flows/new',
        title: 'New troubleshooting flow',
        canActivate: [permissionGuard('kb.author')],
        canDeactivate: [confirmLeave],
        loadComponent: () => import('./flows/flow-designer.page').then((m) => m.FlowDesignerPage),
      },
      {
        path: 'flows/:id',
        title: 'Troubleshooting flow',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.flows)],
        canDeactivate: [confirmLeave],
        loadComponent: () => import('./flows/flow-designer.page').then((m) => m.FlowDesignerPage),
      },
      {
        path: 'analytics',
        title: 'Knowledge Base analytics',
        canActivate: [permissionGuard(...KB_SECTION_PERMISSIONS.analytics)],
        loadComponent: () => import('./analytics/kb-analytics.page').then((m) => m.KbAnalyticsPage),
      },
    ],
  },
];

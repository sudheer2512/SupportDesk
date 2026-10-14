import { Routes } from '@angular/router';

/** Knowledge base: index, category pages (/kb/c/:slug) and articles (/kb/:slug). */
export const ROUTES: Routes = [
  { path: '', title: 'Knowledge Base', loadComponent: () => import('./kb-home.page').then((m) => m.KbHomePage) },
  { path: 'c/:slug', title: 'Knowledge Base', loadComponent: () => import('./kb-category.page').then((m) => m.KbCategoryPage) },
  { path: ':slug', title: 'Article', loadComponent: () => import('./kb-article.page').then((m) => m.KbArticlePage) },
];

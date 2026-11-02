import { DestroyRef, inject } from '@angular/core';
import { Router } from '@angular/router';
import {
  AVAILABILITIES,
  AuthService,
  CustomersApi,
  MeApi,
  MeStore,
  ShortcutRegistry,
  ThemeService,
  TicketsApi,
} from '@sd/core';
import { CommandPaletteService, SdCommand } from '@sd/ui';
import { catchError, map, of } from 'rxjs';
import { DESK_MODULES } from '../nav';

/**
 * Registers the desk's global commands (palette), keyboard shortcuts and the async ticket search
 * provider. Call from the authenticated shell (injection context); everything is unregistered on destroy.
 */
export function registerDeskCommands(): void {
  const router = inject(Router);
  const palette = inject(CommandPaletteService);
  const shortcuts = inject(ShortcutRegistry);
  const me = inject(MeStore);
  const theme = inject(ThemeService);
  const auth = inject(AuthService);
  const meApi = inject(MeApi);
  const tickets = inject(TicketsApi);
  const customers = inject(CustomersApi);

  const go = (url: string) => () => void router.navigateByUrl(url);

  const commands: SdCommand[] = [
    ...DESK_MODULES.map<SdCommand>((m) => ({
      id: `nav.${m.path}`,
      title: `Go to ${m.label}`,
      subtitle: m.description,
      group: 'Navigation',
      icon: m.icon,
      shortcut: m.shortcut ?? null,
      keywords: [m.label, m.path],
      when: () => me.canAny(m.permissions),
      run: go(`/${m.path}`),
    })),
    {
      id: 'nav.notifications',
      title: 'Go to Notifications',
      subtitle: 'Every notification, with unread and needs-acknowledgement filters.',
      group: 'Navigation',
      icon: 'bell',
      keywords: ['alerts', 'unread', 'acknowledge'],
      run: go('/notifications'),
    },
    {
      id: 'nav.approvals',
      title: 'Go to Approvals',
      subtitle: 'Requests waiting for a decision and the ones you asked for.',
      group: 'Navigation',
      icon: 'shield-check',
      keywords: ['approve', 'reject', 'requests'],
      run: go('/approvals'),
    },
    {
      id: 'ticket.new',
      title: 'New ticket',
      group: 'Actions',
      icon: 'file-plus',
      shortcut: 'c',
      keywords: ['create', 'add', 'raise'],
      when: () => me.can('tickets.create'),
      run: go('/tickets/new'),
    },
    { id: 'me.profile', title: 'My profile', group: 'Account', icon: 'user', run: go('/me') },
    { id: 'me.password', title: 'Change password', group: 'Account', icon: 'key', run: go('/me?section=security') },
    { id: 'me.mfa', title: 'Two-factor authentication', group: 'Account', icon: 'shield-check', run: go('/me?section=mfa') },
    ...AVAILABILITIES.map<SdCommand>((a) => ({
      id: `me.availability.${a}`,
      title: `Set availability: ${a}`,
      group: 'Account',
      icon: 'circle-dot',
      when: () => me.availability() !== a,
      run: () => {
        meApi.setAvailability(a).subscribe();
      },
    })),
    {
      id: 'ui.theme',
      title: 'Toggle light / dark theme',
      group: 'Preferences',
      icon: 'sun-moon',
      keywords: ['dark mode', 'appearance'],
      run: () => theme.toggle(),
    },
    {
      id: 'ui.shortcuts',
      title: 'Keyboard shortcuts',
      group: 'Preferences',
      icon: 'keyboard',
      shortcut: '?',
      run: () => shortcuts.helpOpen.set(true),
    },
    { id: 'auth.logout', title: 'Sign out', group: 'Account', icon: 'log-out', run: () => auth.logout() },
  ];

  const unregisterCommands = palette.register(commands);

  // Ticket search: "#1042" / "1042" opens by number; free text searches subjects (GET /tickets?q=).
  const unregisterProvider = palette.registerProvider({
    id: 'tickets',
    group: 'Tickets',
    minQueryLength: 2,
    search: (query) => {
      if (!me.can('tickets.view')) return of([]);
      const number = /^#?(\d{1,10})$/.exec(query.trim())?.[1];
      const toCommand = (t: { id: string; displayNumber: string; subject: string; status: string }): SdCommand => ({
        id: `ticket.${t.id}`,
        title: `${t.displayNumber} · ${t.subject}`,
        subtitle: t.status,
        icon: 'ticket',
        run: go(`/tickets/${t.id}`),
      });
      if (number) {
        return tickets.getByNumber(number).pipe(
          map((t) => [toCommand(t)]),
          catchError(() => of([])),
        );
      }
      return tickets.list({ q: query, limit: 6, sort: 'lastActivity', dir: 'desc' }, { silent: true }).pipe(
        map((page) => page.items.map(toCommand)),
        catchError(() => of([])),
      );
    },
  });

  // Customer search: name or code (GET /customers?q=).
  const unregisterCustomerProvider = palette.registerProvider({
    id: 'customers',
    group: 'Customers',
    minQueryLength: 2,
    search: (query) => {
      if (!me.can('customers.view') || /^#?\d{1,10}$/.test(query.trim())) return of([]);
      return customers.list({ q: query, limit: 5 }).pipe(
        map((page) =>
          page.items.map<SdCommand>((c) => ({
            id: `customer.${c.id}`,
            title: c.name,
            subtitle: [c.code, c.tier].filter(Boolean).join(' · ') || null,
            icon: 'building',
            run: go(`/customers/${c.id}`),
          })),
        ),
        catchError(() => of([])),
      );
    },
  });

  const unregisterShortcuts = shortcuts.register([
    ...DESK_MODULES.filter((m) => m.shortcut).map((m) => ({
      id: `nav.${m.path}`,
      keys: m.shortcut!,
      description: `Go to ${m.label}`,
      group: 'Navigation',
      when: () => me.canAny(m.permissions),
      handler: go(`/${m.path}`),
    })),
    { id: 'nav.profile', keys: 'g m', description: 'Go to my profile', group: 'Navigation', handler: go('/me') },
    {
      id: 'ticket.new',
      keys: 'c',
      description: 'Create a ticket',
      group: 'Actions',
      when: () => me.can('tickets.create'),
      handler: go('/tickets/new'),
    },
    { id: 'search.focus', keys: '/', description: 'Search', group: 'General', handler: () => palette.open() },
    { id: 'ui.theme', keys: 'shift+t', description: 'Toggle light / dark theme', group: 'General', handler: () => theme.toggle() },
  ]);

  inject(DestroyRef).onDestroy(() => {
    unregisterCommands();
    unregisterProvider();
    unregisterCustomerProvider();
    unregisterShortcuts();
  });
}

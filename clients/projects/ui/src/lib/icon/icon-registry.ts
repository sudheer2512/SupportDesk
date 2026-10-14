import { isDevMode } from '@angular/core';
import {
  Activity, ArrowDown, ArrowUpDown, BellRing, MessageSquare, PanelLeftClose, PanelLeftOpen, PanelRightClose, PanelRightOpen, RadioTower,
  ArrowLeft, ArrowRight, ArrowUp, AtSign, Archive, Award, Bell, BellOff, Bold, BookOpen, Bookmark, Bot, BrainCircuit, Bug,
  Building, Calendar, ChartColumn, ChartPie, Check, CheckCheck, ChevronDown, ChevronLeft, ChevronRight, ChevronUp, ChevronsLeft, ChevronsUpDown,
  CircleAlert, CircleCheck, CircleDot, CircleQuestionMark, CircleX, ClipboardList, Clock, Code, Columns3, Command, Copy, CreditCard, Database,
  Download, Ellipsis, EllipsisVertical, ExternalLink, Eye, EyeOff, FilePlus, FileText, Flag, Folder, Funnel, Gauge, GitMerge, GitPullRequest,
  Globe, Grip, Headphones, Heading1, Heading2, Heading3, Hourglass, House, Image, Inbox, Info, Italic, Kanban, Key, Keyboard, Languages, Layers,
  LayoutDashboard, LayoutGrid, LifeBuoy, Lightbulb, Link, List, ListChecks, ListFilter, ListOrdered, Lock, LockOpen, LogIn, LogOut, Mail, MapPin,
  Menu, MessageCircle, MessagesSquare, Minus, MonitorSmartphone, Moon, OctagonAlert, Package, PanelLeft, Paperclip, Pause, Pencil, Phone, Play,
  Plug, Plus, Puzzle, QrCode, Quote, Radar, Receipt, Redo2, RefreshCw, Rocket, RotateCcw, Scale, Search, Send, Server, Settings, Share2,
  ShieldAlert, ShieldCheck, SlidersHorizontal, Smartphone, Sparkles, Split, SquareCode, Star, Strikethrough, Sun, SunMoon, Table, Tag,
  ThumbsDown, ThumbsUp, Ticket, Timer, Trash, TrendingDown, TrendingUp, TriangleAlert, Underline, Undo2, Upload, User, UserCheck, UserPlus,
  Users, Video, Wallet, Wand, Webhook, Workflow, X, Zap
} from 'lucide';

/** Lucide icon data: a list of [svg child tag, attributes]. */
export type IconNode = [string, Record<string, string | number>][];

/**
 * Registry of icon names used across SupportDesk → icon data (Lucide, ISC licence, bundled — no CDN).
 * Apps can add their own with {@link registerIcons}. Unknown names render a "help" glyph so gaps are visible.
 */
const registry = new Map<string, IconNode>(Object.entries({
  activity: Activity, 'arrow-down': ArrowDown, 'arrow-left': ArrowLeft, 'arrow-right': ArrowRight, 'arrow-up': ArrowUp, 'at-sign': AtSign,
  archive: Archive, award: Award, bell: Bell, 'bell-off': BellOff, bold: Bold, 'book-open': BookOpen, bookmark: Bookmark, bot: Bot,
  brain: BrainCircuit, bug: Bug, building: Building, calendar: Calendar, 'chart-column': ChartColumn, 'chart-pie': ChartPie, check: Check,
  tick: Check, 'check-check': CheckCheck, 'chevron-down': ChevronDown, 'chevron-left': ChevronLeft, 'chevron-right': ChevronRight,
  'chevron-up': ChevronUp, 'chevrons-left': ChevronsLeft, 'chevrons-up-down': ChevronsUpDown, 'circle-alert': CircleAlert,
  'circle-check': CircleCheck, 'circle-dot': CircleDot, help: CircleQuestionMark, 'circle-x': CircleX, 'clipboard-list': ClipboardList,
  clock: Clock, code: Code, 'code-block': SquareCode, columns: Columns3, command: Command, copy: Copy, 'credit-card': CreditCard,
  database: Database, download: Download, ellipsis: Ellipsis, 'ellipsis-vertical': EllipsisVertical, 'external-link': ExternalLink, eye: Eye,
  'eye-off': EyeOff, 'file-plus': FilePlus, 'file-text': FileText, flag: Flag, folder: Folder, filter: Funnel, gauge: Gauge, merge: GitMerge,
  'git-pull-request': GitPullRequest, globe: Globe, grip: Grip, headphones: Headphones, 'heading-1': Heading1, 'heading-2': Heading2,
  'heading-3': Heading3, hourglass: Hourglass, home: House, image: Image, inbox: Inbox, info: Info, italic: Italic, kanban: Kanban, key: Key,
  keyboard: Keyboard, languages: Languages, layers: Layers, dashboard: LayoutDashboard, grid: LayoutGrid, 'life-buoy': LifeBuoy,
  lightbulb: Lightbulb, link: Link, list: List, 'list-checks': ListChecks, 'list-filter': ListFilter, 'list-ordered': ListOrdered, lock: Lock,
  'lock-open': LockOpen, 'log-in': LogIn, 'log-out': LogOut, mail: Mail, 'map-pin': MapPin, menu: Menu, 'message-circle': MessageCircle,
  messages: MessagesSquare, minus: Minus, devices: MonitorSmartphone, moon: Moon, 'octagon-alert': OctagonAlert, package: Package,
  'collapse-icon': PanelLeft, 'panel-left-close': PanelLeftClose, 'panel-left-open': PanelLeftOpen,
  'panel-right-close': PanelRightClose, 'panel-right-open': PanelRightOpen, 'arrow-up-down': ArrowUpDown, 'bell-ring': BellRing,
  'message-square': MessageSquare, 'radio-tower': RadioTower, paperclip: Paperclip, pause: Pause, pencil: Pencil, phone: Phone, play: Play, plug: Plug, plus: Plus,
  puzzle: Puzzle, 'qr-code': QrCode, quote: Quote, radar: Radar, receipt: Receipt, redo: Redo2, refresh: RefreshCw, rocket: Rocket,
  history: RotateCcw, scale: Scale, search: Search, send: Send, server: Server, settings: Settings, share: Share2,
  'shield-alert': ShieldAlert, 'shield-check': ShieldCheck, sliders: SlidersHorizontal, smartphone: Smartphone, sparkles: Sparkles,
  split: Split, star: Star, strikethrough: Strikethrough, sun: Sun, 'sun-moon': SunMoon, table: Table, tag: Tag, 'thumbs-down': ThumbsDown,
  'thumbs-up': ThumbsUp, ticket: Ticket, timer: Timer, trash: Trash, 'trending-down': TrendingDown, 'trending-up': TrendingUp,
  'triangle-alert': TriangleAlert, underline: Underline, undo: Undo2, upload: Upload, user: User, 'user-check': UserCheck,
  'user-plus': UserPlus, users: Users, video: Video, wallet: Wallet, wand: Wand, webhook: Webhook, workflow: Workflow, x: X, zap: Zap
} as Record<string, IconNode>));

/** Names known at build time (others can be registered at runtime). */
export type SdIconName = string;

export function registerIcons(icons: Record<string, IconNode>): void {
  for (const [name, node] of Object.entries(icons)) registry.set(name, node);
}

export function iconNode(name: string): IconNode {
  const node = registry.get(name);
  if (node) return node;
  if (isDevMode() && name) console.warn(`sd-icon: unknown icon "${name}" (register it in icon-registry.ts)`);
  return CircleQuestionMark as IconNode;
}

export function hasIcon(name: string): boolean {
  return registry.has(name);
}

export function iconNames(): string[] {
  return [...registry.keys()].sort();
}

export type MovementKind = 'receive' | 'issue' | 'return' | 'transfer' | 'writeoff' | 'adjust';

export interface AccessoryItem {
  id: string; name: string; category: string; brand: string | null; model: string | null; sku: string | null;
  unitCost: number | null; reorderLevel: number; returnable: boolean; serialTracked: boolean; notes: string | null; active: boolean;
  quantity: number; faulty: number; low: boolean; movements: number;
  locations: { locationId: string; location: string; quantity: number; faulty: number; low: boolean }[];
}

export interface StockRow {
  id: string; itemId: string; item: string; category: string; brand: string | null; model: string | null; sku: string | null;
  reorderLevel: number; returnable: boolean; serialTracked: boolean; unitCost: number | null;
  locationId: string; location: string; quantity: number; faulty: number; updatedAt: string | null; low: boolean; lowSince: string | null;
}

export interface MovementRow {
  id: number; occurredAt: string; kind: MovementKind; itemId: string; item: string; category: string; quantity: number; faultyChange: number;
  location: string; locationId: string; toLocation: string | null; person: string | null; department: string | null; departmentId: string | null;
  assetId: string | null; device: string | null; condition: string | null; reason: string | null; reference: string | null; vendor: string | null;
  unitCost: number | null; serials: string[]; note: string | null; by: string | null;
}

export interface Holding {
  id: string; personKey: string; person: string; itemId: string; item: string; category: string; returnable: boolean; quantity: number;
  lastIssued: string | null; departmentId: string | null; department: string | null; serials: string[];
}

export interface AccessorySummary {
  items: number; inStock: number; stockValue: number; lowStock: number; issued30d: number; faulty: number; locations: number; outstanding: number; categories: string[];
}

export const KIND: Record<MovementKind, [string, 'ok' | 'warn' | 'crit' | 'info' | '']> = {
  receive: ['Received', 'ok'],
  issue: ['Issued', 'info'],
  return: ['Returned', ''],
  transfer: ['Transferred', ''],
  writeoff: ['Written off', 'crit'],
  adjust: ['Stock count', 'warn'],
};

export const ISSUE_REASONS = ['New joiner', 'Replacement', 'Damaged / not working', 'Lost', 'Additional need', 'Temporary loan', 'Other'];
export const WRITEOFF_REASONS = ['Damaged beyond repair', 'Lost', 'Stolen', 'Expired / obsolete', 'Scrapped', 'Other'];

export const money = (n: number | null | undefined) => (n == null ? '—' : n.toLocaleString(undefined, { maximumFractionDigits: 2 }));

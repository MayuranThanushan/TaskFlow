export function Icon({ name, size = 20 }: { name: string; size?: number }) {
  const shapes: Record<string, string> = {
    overview: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
    projects: 'M3 7V5a2 2 0 0 1 2-2h5l2 3h7a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z',
    activity: 'M4 12a8 8 0 1 0 2-5 M4 3v5h5 M12 8v5l3 2',
    profile: 'M20 21v-2a6 6 0 0 0-6-6h-4a6 6 0 0 0-6 6v2 M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8',
    tasks: 'M9 6h12 M9 12h12 M9 18h12 M3 6l1 1 2-3 M3 12l1 1 2-3 M3 18l1 1 2-3',
    plus: 'M12 5v14 M5 12h14', arrow: 'M5 12h14 M13 6l6 6-6 6',
  }
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={shapes[name] ?? shapes.tasks} /></svg>
}
export function Badge({ value, children }: { value: string; children?: React.ReactNode }) {
  return <span className={'badge badge-' + value.toLowerCase()}>{children ?? value}</span>
}

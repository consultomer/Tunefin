/** Minimal TTL cache that also coalesces concurrent loads for the same key. */
export class TtlCache<V> {
  private readonly values = new Map<string, { value: V; expires: number }>();
  private readonly pending = new Map<string, Promise<V>>();

  constructor(
    private readonly ttlMs: number,
    private readonly maxEntries = 1000,
  ) {}

  get(key: string): V | undefined {
    const hit = this.values.get(key);
    if (!hit) return undefined;
    if (hit.expires <= Date.now()) {
      this.values.delete(key);
      return undefined;
    }
    return hit.value;
  }

  set(key: string, value: V, ttlMs = this.ttlMs): void {
    if (this.values.size >= this.maxEntries) {
      // Evict the oldest insertion (Map preserves insertion order).
      const oldest = this.values.keys().next().value;
      if (oldest !== undefined) this.values.delete(oldest);
    }
    this.values.set(key, { value, expires: Date.now() + ttlMs });
  }

  delete(key: string): void {
    this.values.delete(key);
  }

  async getOrLoad(key: string, loader: () => Promise<V>, ttlFor?: (v: V) => number): Promise<V> {
    const cached = this.get(key);
    if (cached !== undefined) return cached;
    const inFlight = this.pending.get(key);
    if (inFlight) return inFlight;

    const p = loader()
      .then((v) => {
        this.set(key, v, ttlFor ? ttlFor(v) : this.ttlMs);
        return v;
      })
      .finally(() => this.pending.delete(key));
    this.pending.set(key, p);
    return p;
  }
}

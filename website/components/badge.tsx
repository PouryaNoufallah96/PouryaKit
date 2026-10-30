// A native badge in two parts, a neutral label and an accented value: the site draws its badges itself and loads none
// from a badge host.
export function Badge({ label, value }: { label: string; value: string }) {
  return (
    <span className="inline-flex overflow-hidden rounded-md border font-medium font-mono text-xs leading-6">
      <span className="bg-muted px-2 text-muted-foreground">{label}</span>
      <span className="bg-primary px-2 text-primary-foreground">{value}</span>
    </span>
  );
}

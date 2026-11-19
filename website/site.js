// The site's only script, loaded in <head> (it is tiny). It marks that scripts run, so sections can fade up once as they
// come into view; without it (or with less motion asked for) they simply show. No inline scripts: the site's security
// policy (netlify.toml) allows only files from the site itself.
document.documentElement.classList.add('js');
document.addEventListener('DOMContentLoaded', () => {
  const items = document.querySelectorAll('.reveal');
  if (!('IntersectionObserver' in window) || matchMedia('(prefers-reduced-motion: reduce)').matches) { items.forEach(e => e.classList.add('seen')); return; }
  const io = new IntersectionObserver(entries => entries.forEach(e => { if (e.isIntersecting) { e.target.classList.add('seen'); io.unobserve(e.target); } }), { rootMargin: '0px 0px -8% 0px' });
  items.forEach(e => io.observe(e));
});

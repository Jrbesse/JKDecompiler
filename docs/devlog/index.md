---
layout: default
title: Dev log
permalink: /devlog/
---

# Dev log

The engineering record for JKDecompiler: the recovery and fit stages, the acceptance gates, and the honest unknowns.

<ul>
{% for post in site.posts %}
  <li>
    <strong><a href="{{ post.url | relative_url }}">{{ post.title }}</a></strong><br>
    <small>{{ post.date | date: "%B %-d, %Y" }}</small>
    <p>{{ post.excerpt | strip_html | normalize_whitespace | truncate: 320 }}</p>
  </li>
{% endfor %}
</ul>

---

The acceptance and validation documents live in the repository (`docs/M2.5-ACCEPTANCE.md`,
`docs/M2.5-SP-VALIDATION.md`) rather than on this site: they are working documents, and this is the public record of
what got built from them.

# TODO

A running list of things to check, fix, or add. Add items freely — newest at the top of each section. When something is done, delete it. After each completed item, push and commit the changes.

## To do

### High Priority

### Medium Priority

- **More statuses.** Have more status for example on a series, instead of Missing and Downloaded. We could have queued for the ones about to get downloaded. Can probably have more at other places where we have statuses, feel free to find and improve other areas as well.
- **Monitor status on main library page.** Show the monitor status on all series cards.
- **Make the "Search releases (Prowlarr)" modal larger.** Long titles gets cut off so it is hard to know what the release contains.
- **Make the api much more memory friendly.** Sometimes the memory can build up to over 500MB. I would like to keep it at around MAX 200-300MB.
- **Improve filters for discovery.** Add things like genre, chapter count, etc

### Low Priority

- **Update readme.** Update readme to include everything, and also add a section on how to build a docker image.
- **Fix download button text top right.** The numbers are cut off by what looks like a radius.
- **Add different themes.** Add more themes (current stays default). For example changing the accent color to pink, or creating a light theme. Create a few themes that would fit the application and have them available under Settings.
- **Recommendation Engine v3 phase 4 (optional).** Collaborative-filtering channel trained offline from MAL list dumps, joined via `source_mal_id`, as a centered additive bonus (0 for titles without a CF vector so new manga aren't penalized). Only if the shipped weighted-tag channel (v3 phases 1–3) leaves quality on the table. Plan: https://claude.ai/code/artifact/3e746a8e-cb5f-4832-a4fe-72b1a9d4e4f3

## Known issues / to investigate

- **MangaBaka icon is wrong.** The MangaBaka icon used is not the correct one. Check the favicon for MangaBaka.org
- **On *DiscoverDetailModal* letters are used for abbriviation instead of icons for the scoring.**
- **MAL reviews do not work on DiscoverDetailModal.** They never show any reviews. When it was implemented, Opus 4.8 said that the api was temporarily unavailable but that can't be as it's been several hours and the MAL website itself works just fine with reviews. I believe the implementation to get reviews must be wrong.
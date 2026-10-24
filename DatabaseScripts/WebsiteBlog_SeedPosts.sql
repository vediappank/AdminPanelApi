/* Website Blog - 10 starter posts (real UAE updates, sources linked in each post).
   Run AFTER WebsiteBlog_SuperAdminPanel.sql. Copy the images in App_Data/blog-images to the
   API's blog image folder (Website:BlogImagePath, default <api>/App_Data/blog-images).
   Safe to re-run: existing slugs are skipped. */
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'uae-eases-e-invoicing-rules-ministerial-decision-197-2026' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'UAE eases e-invoicing rules: what Ministerial Decision 197 of 2026 changes', 'uae-eases-e-invoicing-rules-ministerial-decision-197-2026', N'The Ministry of Finance has relaxed three e-invoicing requirements ahead of the January 2027 go-live: a 24-month break for tax-group transactions, overseas data storage and no more 2-day system-failure notices.',
N'<p>The UAE Ministry of Finance has issued <strong>Ministerial Decision No. 197 of 2026</strong>, amending the electronic invoicing rules before the system goes live in January 2027. The amendments took effect on <strong>1 October 2026</strong> and, according to the Ministry, are meant to give "greater clarity" and reduce the operational burden on businesses.</p>
<h2>The three changes</h2>
<ol>
<li><strong>Tax-group transactions are excluded for 24 months.</strong> From 1 January 2027, supplies between members of the same VAT tax group do not need e-invoices until the end of December 2028.</li>
<li><strong>Data can be stored outside the UAE.</strong> The earlier rule that e-invoice and credit-note data must be kept inside the country has been removed. You still have to keep the records for the period set by the Tax Procedures Law and produce them to the Federal Tax Authority (FTA) on request.</li>
<li><strong>No more two-day system-failure notice.</strong> The requirement to tell the FTA within two business days when your e-invoicing system fails has been repealed (Article 12 and its definition in Article 1).</li>
</ol>
<h2>Accreditation of service providers</h2>
<p>A separate decision, <strong>Ministerial Decision No. 168 of 2026</strong> (also effective 1 October 2026), removed the "preliminary approval" stage for e-invoicing service providers. Providers that already held preliminary approval had 30 days from 1 October to complete the full accreditation requirements.</p>
<h2>Key dates to plan around</h2>
<ul>
<li><strong>January 2027</strong> - e-invoicing implementation begins; tax-group exclusion starts 1 January 2027.</li>
<li><strong>1 July 2027</strong> - mandatory for smaller businesses.</li>
<li><strong>1 October 2027</strong> - mandatory for government entities.</li>
<li><strong>31 December 2028</strong> - tax-group exclusion ends.</li>
</ul>
<h2>What you should do now</h2>
<ul>
<li>Confirm which phase your business falls into and when your mandatory date is.</li>
<li>Choose an accredited service provider and check your accounting software can connect to it.</li>
<li>If you are in a tax group, keep issuing proper tax invoices between members - only the e-invoicing requirement is paused.</li>
<li>If you plan to store data abroad, make sure you can still retrieve it quickly for an FTA request.</li>
</ul>
<p><strong>Need help?</strong> Our accounting team can check your readiness and set up your invoicing flow. <a href="https://blisspointworld.com/accounting/services/fta-compliance.html">Talk to us about FTA compliance</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://gulfnews.com/business/banking/uae-eases-e-invoicing-rules-businesses-to-now-get-more-flexibility-time-1.500715708" target="_blank" rel="noopener">Gulf News - UAE eases e-invoicing rules (19 Oct 2026)</a></li><li><a href="https://gulfnews.com/uae/dubai/5-major-uae-rule-changes-announced-in-october-what-residents-need-to-know-1.500705788" target="_blank" rel="noopener">Gulf News - 5 major UAE rule changes announced in October</a></li></ul>',
'202610_deabbe54d7668e21802d96a44c142d56.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'government-updates'), N'E-invoicing, VAT, Ministry of Finance, FTA', 'Published', '2026-10-20 06:00', N'Bliss Point Team',
N'UAE eases e-invoicing rules: what Ministerial Decision 197 of 2026 cha', N'The Ministry of Finance has relaxed three e-invoicing requirements ahead of the January 2027 go-live: a 24-month break for tax-group transactions, overseas data storage and no more 2-day system-failure notices.', 1, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'uae-vat-changes-october-2026-input-tax-due-diligence' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'UAE VAT changes from 1 October 2026: supplier checks before you recover input tax', 'uae-vat-changes-october-2026-input-tax-due-diligence', N'Cabinet Decision 149 and FTA Decision 13 of 2026 change how input VAT is recovered. Here is what the new supplier due-diligence checks mean, plus the October and November FTA filing deadlines.',
N'<p>Two decisions that took effect on <strong>1 October 2026</strong> change how UAE businesses recover input VAT:</p>
<ul>
<li><strong>Cabinet Decision No. 149 of 2026</strong>, which amends the VAT Executive Regulation (Cabinet Decision No. 52 of 2017).</li>
<li><strong>FTA Decision No. 13 of 2026</strong>, which sets out mandatory due-diligence checks on suppliers and supplies.</li>
</ul>
<p>Both build on the amended VAT Law (Federal Decree-Law No. 16 of 2025), in force since 1 January 2026.</p>
<h2>Input tax can be refused if you did not check your supplier</h2>
<p>The FTA can deny input tax recovery when a supply is linked to a chain connected with tax evasion. Refusal is mandatory if you knew, and discretionary if you should have known. Not verifying a supply is treated as if you knew. In practice, you need evidence of your checks:</p>
<ul>
<li>Supplier identity, authorised signatories and a real place of business.</li>
<li>Written bank confirmation for suppliers you buy more than AED 375,000 a year from.</li>
<li>A commercial reason for each supply, with payment made electronically. Cash needs a documented reason.</li>
<li>A written internal policy naming who performs, reviews and approves the checks.</li>
<li>Re-verification of each supplier every 12 months.</li>
</ul>
<p>Small purchases under AED 10,000 (excluding VAT) are exempt from the checks. The exemption stops once your purchases from that supplier pass AED 100,000 in 12 months.</p>
<h2>Other changes in the Executive Regulation</h2>
<ul>
<li><strong>Employee benefits</strong> - VAT on benefits required by labour law is recoverable. VAT on staff accommodation is restricted unless a MoHRE decision requires you to provide it.</li>
<li><strong>Excess input tax</strong> can be carried forward for five years. Unused amounts then expire.</li>
<li><strong>Apportionment</strong> moves to an output-based method from the first tax year starting after 1 October 2027.</li>
</ul>
<h2>Upcoming FTA deadlines</h2>
<p>The FTA has announced <strong>28 October 2026</strong> as the final deadline for VAT returns due this cycle, and <strong>16 November 2026</strong> for excise tax returns. File through EmaraTax before these dates to avoid late-filing penalties.</p>
<p><strong>Need help?</strong> We prepare VAT returns and can build the supplier-check file the FTA now expects. <a href="https://blisspointworld.com/accounting/services/vat-filing.html">See our VAT filing service</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://www.techvaria.com/blog/uae-vat-changes-october-2026-zoho-books-odoo.html" target="_blank" rel="noopener">Techvaria - UAE VAT changes October 2026 (21 Oct 2026)</a></li><li><a href="https://tax.gov.ae/en/announcements.aspx" target="_blank" rel="noopener">Federal Tax Authority - Announcements</a></li></ul>',
'202610_1f00d86e6e8e38209cb45509f37cf215.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'accounting-tax'), N'VAT, Input tax, FTA, Due diligence', 'Published', '2026-10-22 06:00', N'Bliss Point Team',
N'UAE VAT changes from 1 October 2026: supplier checks before you recove', N'Cabinet Decision 149 and FTA Decision 13 of 2026 change how input VAT is recovered. Here is what the new supplier due-diligence checks mean, plus the October and November FTA filing deadlines.', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'dubai-shared-housing-law-4-of-2026-what-owners-and-tenants-must-know' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'Dubai shared housing law (Law No. 4 of 2026): what owners and tenants must know', 'dubai-shared-housing-law-4-of-2026-what-owners-and-tenants-must-know', N'Dubai Law No. 4 of 2026 sets new rules for shared accommodation: designated areas only, space and bathroom standards, and a one-year deadline for existing properties.',
N'<p><strong>Dubai Law No. 4 of 2026</strong> on shared housing came into force in August 2026. It sets clear rules for where and how shared accommodation can be offered. If you own, rent out or live in a shared unit, here is what changes.</p>
<h2>The main rules</h2>
<ul>
<li><strong>No mixing families and individuals.</strong> Families and individuals cannot share the same residential unit.</li>
<li><strong>Designated areas only.</strong> Shared accommodation for individuals is allowed only in approved areas. Dubai Municipality has approved more than 44 areas, fully or partly. Being in one of these areas does not automatically make a building eligible.</li>
<li><strong>Family-only main roads.</strong> Individual shared housing is banned along Sheikh Zayed Road, Jumeirah Road, Al Wasl Road and Baniyas Road.</li>
<li><strong>Minimum standards.</strong> At least 5 sq m of bedroom space per person, one full bathroom for every four occupants, and a separate bedroom with a private bathroom for each family.</li>
<li><strong>Permits.</strong> Owners must get the required permits and meet occupancy, planning and safety standards.</li>
</ul>
<h2>Deadline for existing properties</h2>
<p>Owners have <strong>one year</strong> from the law coming into force to bring existing properties into line. Building violations and unauthorised changes of use must be fixed even during this grace period. Reports on the law mention fines of up to <strong>Dh1 million</strong> for serious violations.</p>
<h2>What to do</h2>
<ul>
<li><strong>Landlords:</strong> check whether your building is in an approved area and apply for the required permit.</li>
<li><strong>Companies housing staff:</strong> review staff accommodation against the space and bathroom standards.</li>
<li><strong>Tenants:</strong> before signing, ask the landlord whether the unit is approved for shared use.</li>
</ul>
<p><strong>Need help?</strong> Our PRO team can handle permit applications and municipality paperwork for you. <a href="https://blisspointworld.com/business/services/pro-services.html">See our PRO services</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://gulfnews.com/uae/dubai/5-major-uae-rule-changes-announced-in-october-what-residents-need-to-know-1.500705788" target="_blank" rel="noopener">Gulf News - 5 major UAE rule changes announced in October (10 Oct 2026)</a></li></ul>',
'202610_f2cdcf2fffe3c1d40156d33d115b1ce7.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'government-updates'), N'Dubai, Housing, Dubai Municipality, Tenants', 'Published', '2026-10-12 06:00', N'Bliss Point Team',
N'Dubai shared housing law (Law No. 4 of 2026): what owners and tenants ', N'Dubai Law No. 4 of 2026 sets new rules for shared accommodation: designated areas only, space and bathroom standards, and a one-year deadline for existing properties.', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'uae-small-business-relief-corporate-tax-extended-to-2029' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'Corporate tax Small Business Relief extended to the end of 2029', 'uae-small-business-relief-corporate-tax-extended-to-2029', N'Ministerial Decision No. 131 lets businesses with revenue up to AED 3 million keep using Small Business Relief for tax periods ending on or before 31 December 2029.',
N'<p>On 7 August 2026 the UAE Ministry of Finance announced <strong>Ministerial Decision No. 131</strong>, extending corporate tax <strong>Small Business Relief</strong> (SBR). Eligible businesses can now use it for tax periods ending on or before <strong>31 December 2029</strong>.</p>
<h2>Who qualifies</h2>
<ul>
<li>Resident businesses with annual revenue of <strong>AED 3 million or less</strong>.</li>
<li>The business must meet the other conditions in the corporate tax legislation. Check your eligibility each tax period, because revenue above AED 3 million in any period takes you out of the relief.</li>
</ul>
<h2>What the relief means</h2>
<p>Businesses that elect for SBR are treated as having no taxable income for that period and use simplified compliance. <strong>You still have to register for corporate tax and file a return</strong> - the relief is claimed in the return.</p>
<h2>Corporate tax basics</h2>
<ul>
<li>Standard rate: <strong>9%</strong> on taxable income above <strong>AED 375,000</strong>. Income up to that level is taxed at 0%.</li>
<li>It applies from financial years starting on or after 1 June 2023.</li>
<li>Individuals running a business are only subject to corporate tax if their business turnover exceeds AED 1 million in a calendar year.</li>
<li>Returns are due within <strong>nine months</strong> of the end of each tax period.</li>
</ul>
<h2>What to do</h2>
<ul>
<li>Track your revenue each year against the AED 3 million limit.</li>
<li>Keep proper books - the FTA uses your records to verify SBR eligibility.</li>
<li>Elect SBR in each return where you qualify; it is not automatic.</li>
</ul>
<p><strong>Need help?</strong> We register businesses for corporate tax and file SBR returns every year. <a href="https://blisspointworld.com/accounting/services/corporate-tax.html">See our corporate tax service</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://thenationalnews.com/business/economy/2026/08/07/uae-extends-corporate-tax-relief-for-small-businesses-until-2029" target="_blank" rel="noopener">The National - UAE extends corporate tax relief for small businesses until 2029 (7 Aug 2026)</a></li><li><a href="https://gulfnews.com/business/corporate-tax/uae-extends-corporate-tax-relief-for-small-businesses-until-end-of-2029-1.500633548" target="_blank" rel="noopener">Gulf News - UAE extends corporate tax relief for small businesses until end of 2029</a></li></ul>',
'202610_f5e222044b058e6020886806572ec84b.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'accounting-tax'), N'Corporate Tax, Small Business Relief, SME, Ministry of Finance', 'Published', '2026-08-10 06:00', N'Bliss Point Team',
N'Corporate tax Small Business Relief extended to the end of 2029', N'Ministerial Decision No. 131 lets businesses with revenue up to AED 3 million keep using Small Business Relief for tax periods ending on or before 31 December 2029.', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'corporate-tax-return-deadline-30-september-2026-what-now' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'Corporate tax returns for 2025: the 30 September deadline and what to do if you missed it', 'corporate-tax-return-deadline-30-september-2026-what-now', N'Businesses with a 31 December 2025 year end had to file and pay corporate tax by 30 September 2026. Here is the nine-month rule, who had to file, and the steps to take if you are late.',
N'<p>For businesses with a tax period ending <strong>31 December 2025</strong>, the corporate tax return and payment were due by <strong>30 September 2026</strong>. The Federal Tax Authority (FTA) reminded businesses about this deadline throughout September.</p>
<h2>The nine-month rule</h2>
<p>A corporate tax return must be filed, and any tax paid, <strong>within nine months of the end of the tax period</strong>. If your financial year ends on another date, your deadline moves with it. For example, a 31 March 2026 year end must file by 31 December 2026.</p>
<h2>Who had to file</h2>
<ul>
<li>All taxable persons whose tax period ended on 31 December 2025.</li>
<li>Businesses claiming <strong>Small Business Relief</strong>, which file a simplified return.</li>
<li>Exempt persons that must register, which file an annual declaration within nine months of their financial year end.</li>
</ul>
<h2>If you missed the deadline</h2>
<ol>
<li><strong>File as soon as possible</strong> on EmaraTax (available 24/7) or through an FTA-approved tax agent. Late-filing penalties increase with every month of delay.</li>
<li><strong>Pay any tax due</strong> to stop late-payment penalties from growing.</li>
<li><strong>Check your registration.</strong> If you were never registered, register first - late registration carries its own penalty.</li>
<li><strong>Keep your records.</strong> The FTA uses your documents to verify revenue, taxable income and SBR eligibility. Missing records attract separate penalties.</li>
</ol>
<h2>Plan the next return now</h2>
<ul>
<li>Close your books monthly so year-end accounts are ready early.</li>
<li>Put the next filing date in your calendar the day your year ends.</li>
<li>Get financial statements prepared, and audited where required, well before month nine.</li>
</ul>
<p><strong>Need help?</strong> Late or not, we can prepare and file your corporate tax return. <a href="https://blisspointworld.com/accounting/services/corporate-tax-filing.html">See our corporate tax filing service</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://gulfnews.com/business/corporate-tax/september-30-deadline-set-for-uae-corporate-tax-returns-and-payments-1.500688857" target="_blank" rel="noopener">Gulf News - September 30 deadline set for UAE corporate tax returns (26 Sep 2026)</a></li><li><a href="https://tax.gov.ae/en/legislation/corporate.tax.aspx" target="_blank" rel="noopener">Federal Tax Authority - Corporate Tax legislation</a></li></ul>',
'202610_dd3159f7ec8111bbea5607544c03ff79.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'accounting-tax'), N'Corporate Tax, EmaraTax, FTA, Deadlines', 'Published', '2026-10-05 06:00', N'Bliss Point Team',
N'Corporate tax returns for 2025: the 30 September deadline and what to ', N'Businesses with a 31 December 2025 year end had to file and pay corporate tax by 30 September 2026. Here is the nine-month rule, who had to file, and the steps to take if you are late.', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'uae-visa-on-arrival-extended-to-six-more-nationalities' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'UAE extends visa on arrival to six more nationalities', 'uae-visa-on-arrival-extended-to-six-more-nationalities', N'Since 25 June 2026, nationals of Indonesia, the Philippines, Vietnam, Thailand, Kenya and South Africa with residence in the US, UK, EU and six other countries can get a UAE visa on arrival.',
N'<p>From <strong>25 June 2026</strong>, the UAE grants visa on arrival to ordinary-passport holders from <strong>six more countries</strong>, as long as they hold a valid residence permit from an approved country. The Federal Authority for Identity, Citizenship, Customs and Port Security (ICP) published the details, and the Ministry of Foreign Affairs announced the change.</p>
<h2>Newly eligible nationalities</h2>
<ul>
<li>Indonesia</li>
<li>Philippines</li>
<li>Vietnam</li>
<li>Thailand</li>
<li>Kenya</li>
<li>South Africa</li>
</ul>
<h2>Residence you must hold</h2>
<p>A valid residence permit from one of these countries: the <strong>United States, United Kingdom, European Union states, Singapore, Japan, South Korea, Australia, New Zealand or Canada</strong>. Before this change, visa on arrival based on these residencies was limited to eligible Indian nationals.</p>
<h2>Visa options and fees</h2>
<ul>
<li><strong>14-day visa - AED 100.</strong> It can be extended once while you are in the UAE.</li>
<li><strong>60-day visa - AED 250.</strong> It covers a single stay and cannot be extended.</li>
</ul>
<p>Leave before your visa expires: overstaying is fined <strong>AED 50 per day</strong>.</p>
<h2>Tips before you fly</h2>
<ul>
<li>Check your passport and residence permit are both valid for your whole trip.</li>
<li>Keep your return ticket and hotel booking handy for immigration.</li>
<li>If you are not eligible, a visit visa must be issued before you travel.</li>
</ul>
<p><strong>Need help?</strong> Not eligible for visa on arrival? We arrange tourist and visit visas for family and business guests. <a href="https://blisspointworld.com/business/services/visa-services.html">See our visa services</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://www.dubaieye1038.com/news/local/uae-offers-visa-on-arrival-to-nationals-of-6-new-countries/" target="_blank" rel="noopener">Dubai Eye 103.8 - UAE offers visa-on-arrival to nationals of 6 new countries (25 Jun 2026)</a></li><li><a href="https://www.khaleejtimes.com/life-and-living/visa-and-immigration-in-uae/uae-visa-rule-changes-2026" target="_blank" rel="noopener">Khaleej Times - 6 UAE visa rule changes in 2026</a></li></ul>',
'202610_afc71fe5544507a469d6ce7e11690b73.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'visas'), N'Visa on arrival, ICP, Tourist visa', 'Published', '2026-06-27 06:00', N'Bliss Point Team',
N'UAE extends visa on arrival to six more nationalities', N'Since 25 June 2026, nationals of Indonesia, the Philippines, Vietnam, Thailand, Kenya and South Africa with residence in the US, UK, EU and six other countries can get a UAE visa on arrival.', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'dubai-visa-updates-2026-48-hour-tourist-visa-property-residency' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'Dubai visa updates 2026: 48-hour tourist visas and new property residency rules', 'dubai-visa-updates-2026-48-hour-tourist-visa-property-residency', N'GDRFA Dubai now approves single-entry tourist visas within 48 hours, and the AED 750,000 minimum for the two-year property-linked residency has been removed. Here is what changed this year.',
N'<p>Dubai has made several visa changes in 2026. These are the ones residents, investors and visiting families ask us about most.</p>
<h2>1. Tourist visas approved within 48 hours</h2>
<p>The General Directorate of Identity and Foreigners Affairs - Dubai (GDRFA) now approves <strong>single-entry tourist visas within 48 hours</strong>. The visas are valid for <strong>30 to 60 days</strong>, and applications must go through authorised tourism offices.</p>
<h2>2. Two-year property-linked residency</h2>
<ul>
<li>The <strong>AED 750,000 minimum property value</strong> for individual investors has been removed.</li>
<li>The applicant must be the <strong>sole owner</strong>.</li>
<li>For jointly owned property, each owner''s share must be worth at least <strong>AED 400,000</strong>, even when the property is split equally.</li>
</ul>
<h2>3. Smart medical visa (planned)</h2>
<p>GDRFA Dubai and the Dubai Health Authority signed an agreement to develop a <strong>smart medical visa</strong> that links visa, residency and healthcare services for medical tourists. No launch date has been announced yet.</p>
<h2>4. Visa on arrival for six more nationalities</h2>
<p>From 25 June 2026, nationals of Indonesia, the Philippines, Vietnam, Thailand, Kenya and South Africa holding residence in the US, UK, EU and other approved countries can get a visa on arrival.</p>
<h2>5. Suspension for some nationalities</h2>
<p>From <strong>6 June 2026</strong>, new visas for nationals of the Democratic Republic of the Congo, Uganda and South Sudan are suspended because of the Ebola outbreak. The suspension may be extended depending on the health situation.</p>
<h2>How we can help</h2>
<ul>
<li>Tourist and visit visas for family and business guests.</li>
<li>Property investor residency applications, including the documents for joint ownership.</li>
<li>Status changes, renewals and cancellations.</li>
</ul>
<p><strong>Need help?</strong> Tell us your situation and we will tell you which visa fits. <a href="https://blisspointworld.com/business/services/visa-services.html">See our visa services</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://www.khaleejtimes.com/life-and-living/visa-and-immigration-in-uae/uae-visa-rule-changes-2026" target="_blank" rel="noopener">Khaleej Times - 6 UAE visa rule changes in 2026 (updated 30 Aug 2026)</a></li></ul>',
'202610_be6fcd8eb8d470a9b79f56122b7d1a35.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'visas'), N'Dubai, GDRFA, Tourist visa, Residence visa, Property', 'Published', '2026-07-06 06:00', N'Bliss Point Team',
N'Dubai visa updates 2026: 48-hour tourist visas and new property reside', N'GDRFA Dubai now approves single-entry tourist visas within 48 hours, and the AED 750,000 minimum for the two-year property-linked residency has been removed. Here is what changed this year.', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'emiratisation-2026-year-end-target-december-31' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'Emiratisation 2026: meet the 2% target by 31 December', 'emiratisation-2026-year-end-target-december-31', N'Private companies with 50 or more employees must raise Emirati staff in skilled roles by 2% in 2026. Here are the targets, the AED 10,000-a-month contribution for missed positions, and the incentives.',
N'<p>The Ministry of Human Resources and Emiratisation (MoHRE) requires private companies with <strong>50 or more employees</strong> to increase the number of UAE nationals in skilled jobs by <strong>2% in 2026</strong>. The target is split into two halves.</p>
<h2>The 2026 targets</h2>
<ul>
<li><strong>First half: +1%</strong> by 30 June 2026.</li>
<li><strong>Second half: +1%</strong> by 31 December 2026, bringing the total to 2% for the year.</li>
</ul>
<h2>What happens if you miss it</h2>
<p>For the first-half target, MoHRE said non-compliant companies pay <strong>AED 10,000 per month for each unfilled position</strong> from 1 July 2026, up to AED 120,000 a year. Companies that miss targets can also face a lower MoHRE classification and required corrective action.</p>
<p>MoHRE treats "fake Emiratisation" - hiring nationals on paper only - as a serious violation. Violations can be reported on 600590000, the MoHRE app or the MoHRE website.</p>
<h2>Smaller companies</h2>
<p>Companies with 20 to 49 employees in specific fast-growing sectors must also employ at least one UAE national and keep existing Emirati staff. Check whether your activity is on MoHRE''s list.</p>
<h2>Incentives for meeting targets</h2>
<ul>
<li>Membership of the Emiratisation Partners Club, with up to <strong>80% off MoHRE service fees</strong>.</li>
<li>Priority in government procurement.</li>
<li>Support through the <strong>Nafis</strong> platform, which lists Emirati job seekers.</li>
</ul>
<h2>Checklist before 31 December</h2>
<ul>
<li>Count your skilled roles and work out how many Emiratis you need.</li>
<li>Post vacancies on Nafis early - hiring and onboarding take time.</li>
<li>Make sure new hires are registered with pension and on the WPS payroll.</li>
</ul>
<p><strong>Need help?</strong> Our PRO team handles MoHRE work permits, contracts and Emiratisation registration. <a href="https://blisspointworld.com/business/services/pro-services.html">See our PRO services</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://www.khaleejtimes.com/emiratisation-target-june-20-deadline-fake-schemes-fines" target="_blank" rel="noopener">Khaleej Times - June 30 deadline for Emiratisation targets (22 Jun 2026)</a></li><li><a href="https://gulfnews.com/uae/meet-emiratisation-targets-by-december-31-or-face-fines-warns-mohre-1.500322794" target="_blank" rel="noopener">Gulf News - Meet Emiratisation targets by December 31 or face fines, warns MoHRE</a></li></ul>',
'202610_637964597588a2e3cb352748cdf01097.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'government-updates'), N'Emiratisation, MoHRE, Nafis, Private sector', 'Published', '2026-10-15 06:00', N'Bliss Point Team',
N'Emiratisation 2026: meet the 2% target by 31 December', N'Private companies with 50 or more employees must raise Emirati staff in skilled roles by 2% in 2026. Here are the targets, the AED 10,000-a-month contribution for missed positions, and the incentives.', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'dubai-free-zone-companies-operate-on-mainland-resolution-11-2025' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'Can a Dubai free zone company work on the mainland? Resolution 11 of 2025 explained', 'dubai-free-zone-companies-operate-on-mainland-resolution-11-2025', N'Dubai Executive Council Resolution No. 11 of 2025 lets free zone companies trade on the Dubai mainland through a branch licence (AED 10,000 a year) or a temporary permit (AED 5,000 for up to six months).',
N'<p>For years, a Dubai free zone company could not sell directly to customers on the mainland without a separate mainland company or a local distributor. <strong>Dubai Executive Council Resolution No. 11 of 2025</strong>, published in the Official Gazette on 3 March 2025, changed that.</p>
<h2>Three ways to operate on the mainland</h2>
<ul>
<li><strong>Mainland branch licence</strong> - set up a branch of your free zone company on the mainland. Valid one year, renewable, <strong>AED 10,000 a year</strong>.</li>
<li><strong>Branch licence operating from the free zone</strong> - carry out mainland activities while staying in your free zone premises. Valid one year, renewable, <strong>AED 10,000 a year</strong>.</li>
<li><strong>Temporary permit</strong> - for a specific activity or project outside the free zone, for up to <strong>six months</strong>, <strong>AED 5,000</strong>.</li>
</ul>
<h2>Conditions</h2>
<ul>
<li>You need prior approval from your free zone authority and, where relevant, other regulators.</li>
<li>The Department of Economy and Tourism (DET) issues the licences and publishes the list of eligible activities.</li>
<li>It covers Dubai free zone companies only. DIFC financial entities are excluded, and operating in other emirates needs their own licence.</li>
<li>You must keep separate financial records for mainland activity and be ready for DET audits.</li>
<li>Businesses already operating outside their free zone get one year to comply (extendable by one year). After that, penalties may apply.</li>
</ul>
<h2>Is it right for you?</h2>
<p>If most of your customers are on the mainland, a full mainland company may still work out better. If you only need occasional mainland projects or want to test the market, the branch licence or temporary permit is usually quicker and cheaper. Check the corporate tax effect too - mainland income can affect a Qualifying Free Zone Person''s 0% rate.</p>
<p><strong>Need help?</strong> We compare free zone, mainland and branch options for your activity and handle the DET application. <a href="https://blisspointworld.com/business/services/free-zone-setup.html">See our free zone setup service</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://kpmg.com/ae/en/insights/tax-insights/dubai-issues-resolution-enabling-free-zone-companies-to-operate-on-mainland.html" target="_blank" rel="noopener">KPMG - Dubai issues resolution enabling free zone companies to operate on mainland</a></li><li><a href="https://www.reedsmith.com/our-insights/blogs/viewpoints/102l0qt/new-era-for-free-zone-mainland-integration-dubais-executive-council-resolution/" target="_blank" rel="noopener">Reed Smith - New era for free zone-mainland integration</a></li></ul>',
'202610_a1e7c8931f96b6356a75784a2430639d.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'business-setup'), N'Free zone, Mainland, DET, Business setup', 'Published', '2026-09-15 06:00', N'Bliss Point Team',
N'Can a Dubai free zone company work on the mainland? Resolution 11 of 2', N'Dubai Executive Council Resolution No. 11 of 2025 lets free zone companies trade on the Dubai mainland through a branch licence (AED 10,000 a year) or a temporary permit (AED 5,000 for up to six months).', 0, N'seed');

IF NOT EXISTS (SELECT 1 FROM dbo.BlogPost WHERE Slug = 'new-uae-consulate-legalisation-format-mofa-attestation' AND IsDeleted = 0)
INSERT dbo.BlogPost (Title, Slug, Summary, ContentHtml, CoverImage, BlogCategoryId, Tags, Status, PublishedAt, AuthorName, MetaTitle, MetaDescription, IsFeatured, CreatedBy)
VALUES (N'New UAE consulate legalisation format: do you still need MOFA attestation?', 'new-uae-consulate-legalisation-format-mofa-attestation', N'UAE embassies and consulates now issue an electronic legalisation page that can include MOFA attestation, so some documents no longer need a separate MOFA stamp in the UAE.',
N'<p>Documents issued abroad - degree certificates, marriage certificates, powers of attorney and company documents - must be legalised before UAE authorities accept them. UAE embassies and consulates abroad have introduced a <strong>new, electronically issued legalisation page</strong>, and in some cases it changes the process.</p>
<h2>What changed</h2>
<ul>
<li>The consulate''s legalisation page is now issued electronically.</li>
<li>In certain cases it <strong>already includes UAE Ministry of Foreign Affairs (MOFA) attestation</strong> alongside the consulate''s attestation.</li>
<li>When it does, you <strong>do not need a separate MOFA attestation in the UAE</strong>, and no extra MOFA fee is payable here.</li>
<li>Official fees for the combined legalisation are unchanged and do not exceed AED 2,300.</li>
</ul>
<h2>The process when MOFA is included</h2>
<ol>
<li>Get the document legalised at the UAE embassy or consulate (the page now includes MOFA attestation).</li>
<li>Have the document translated into Arabic by a certified legal translator.</li>
<li>Have the Arabic translation attested by the Ministry of Justice (MOJ), where required.</li>
</ol>
<h2>Check your page first</h2>
<p>Not every legalisation page includes MOFA attestation. Before you book a MOFA appointment, check the page - or send it to us and we will check it. The change also does not relax the Ministry of Economy and Tourism''s Power of Attorney requirements.</p>
<h2>Documents we commonly handle</h2>
<ul>
<li>Educational certificates for employment and golden visas.</li>
<li>Marriage and birth certificates for family sponsorship.</li>
<li>Company documents and powers of attorney for business setup.</li>
</ul>
<p><strong>Need help?</strong> Send us a photo of your document and we will tell you exactly which steps are still needed. <a href="https://blisspointworld.com/service-attestation.html">See our attestation service</a> or WhatsApp us on +971 58 997 0732.</p>
<h3>Sources</h3><ul><li><a href="https://rouse.com/insights/news/2026/new-uae-consulate-legalisation-format-what-you-need-to-know/" target="_blank" rel="noopener">Rouse - New UAE consulate legalisation format: what you need to know</a></li></ul>',
'202610_7a5406a86c783066facbdf435cc4b56c.webp', (SELECT BlogCategoryId FROM dbo.BlogCategory WHERE Slug = 'typing-documents'), N'Attestation, MOFA, Legalisation, Translation', 'Published', '2026-09-08 06:00', N'Bliss Point Team',
N'New UAE consulate legalisation format: do you still need MOFA attestat', N'UAE embassies and consulates now issue an electronic legalisation page that can include MOFA attestation, so some documents no longer need a separate MOFA stamp in the UAE.', 0, N'seed');

SELECT p.BlogPostId, p.Title, c.Name AS Category, p.PublishedAt, p.CoverImage FROM dbo.BlogPost p LEFT JOIN dbo.BlogCategory c ON c.BlogCategoryId = p.BlogCategoryId WHERE p.CreatedBy = N'seed' ORDER BY p.PublishedAt DESC;
GO

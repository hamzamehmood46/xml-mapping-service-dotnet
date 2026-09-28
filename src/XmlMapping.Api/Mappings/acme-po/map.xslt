<?xml version="1.0" encoding="utf-8"?>
<!-- ACME PurchaseOrder -> internal Order. XSLT 1.0 (no sum-of-products), so the total uses a recursive template. -->
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:po="urn:acme:po"
                exclude-result-prefixes="po"
                xmlns="urn:orderflow:internal">
  <xsl:output method="xml" indent="yes" omit-xml-declaration="yes"/>

  <xsl:template match="/po:PurchaseOrder">
    <Order id="{@orderNo}" placedOn="{@date}" source="ACME">
      <Customer name="{po:Buyer/po:Name}" email="{po:Buyer/po:Email}"/>
      <Lines>
        <xsl:for-each select="po:Items/po:Item">
          <Line sku="{@sku}" quantity="{@qty}" unitPrice="{@price}" lineTotal="{format-number(@qty * @price, '0.00')}"/>
        </xsl:for-each>
      </Lines>
      <Total>
        <xsl:call-template name="sum-lines">
          <xsl:with-param name="items" select="po:Items/po:Item"/>
        </xsl:call-template>
      </Total>
    </Order>
  </xsl:template>

  <xsl:template name="sum-lines">
    <xsl:param name="items"/>
    <xsl:param name="running" select="0"/>
    <xsl:choose>
      <xsl:when test="$items">
        <xsl:call-template name="sum-lines">
          <xsl:with-param name="items" select="$items[position() &gt; 1]"/>
          <xsl:with-param name="running" select="$running + ($items[1]/@qty * $items[1]/@price)"/>
        </xsl:call-template>
      </xsl:when>
      <xsl:otherwise><xsl:value-of select="format-number($running, '0.00')"/></xsl:otherwise>
    </xsl:choose>
  </xsl:template>
</xsl:stylesheet>
